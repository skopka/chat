using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Skopka.Chat.Attachments;
using Skopka.Chat.Server;

namespace Skopka.Chat.ServerTrusted.PostgreSql;

/// <summary>Host configuration of the PostgreSQL server-trusted integration.</summary>
public sealed class ServerTrustedChatPostgreSqlOptions
{
    /// <summary>Connection string of the host database. Required.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Schema holding every table (default <see cref="ServerTrustedChatSchema.Default"/>).</summary>
    public string Schema { get; set; } = ServerTrustedChatSchema.Default;

    /// <summary>
    /// Runs the <c>LISTEN/NOTIFY</c> bridge so live signals reach components on every replica (default true). It needs a
    /// direct or session-pooled connection; PgBouncer transaction pooling cannot carry <c>LISTEN</c>.
    /// </summary>
    public bool EnableNotifyBridge { get; set; } = true;

    /// <summary>Runs the maintenance worker (pending-upload expiry, blob deletions, completed-outbox cleanup). Default true.</summary>
    public bool EnableMaintenance { get; set; } = true;

    /// <summary>Interval of the maintenance worker (default 5 minutes).</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Engine bounds.</summary>
    public ServerTrustedChatOptions Chat { get; } = new();

    /// <summary>Dispatcher bounds used when an event publisher is registered.</summary>
    public ChatServerEventDispatchOptions Dispatch { get; } = new();

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString) || !ServerTrustedChatSchema.IsValid(Schema) ||
            MaintenanceInterval < TimeSpan.FromSeconds(10) || MaintenanceInterval > TimeSpan.FromHours(24))
        {
            throw new ArgumentException("The server-trusted PostgreSQL options are invalid.");
        }

        Chat.Validate();
        Dispatch.Validate();
    }
}

/// <summary>
/// Host-implemented consumer of server-trusted outbox events (for example: enqueue an "unread for 15 minutes" email
/// check). Success means the host durably accepted the event; it must deduplicate by <c>EventId</c>.
/// </summary>
public interface IServerTrustedChatEventPublisher
{
    /// <summary>Publishes one metadata-only event.</summary>
    ValueTask PublishAsync(ChatServerOutboxMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Dependency-injection registration of the PostgreSQL server-trusted mode.</summary>
public static class ServerTrustedChatServiceCollectionExtensions
{
    /// <summary>
    /// Registers the context factory, PostgreSQL store, in-process signal hub, scoped <see cref="ServerTrustedChatService"/>
    /// and the optional bridge/maintenance workers. The host must also register an
    /// <see cref="IServerTrustedChatAccessPolicy"/> and an <see cref="IServerTrustedChatRateLimiter"/>; attachments
    /// additionally need an <see cref="IAttachmentStore"/> and an <see cref="IServerTrustedAttachmentValidator"/>.
    /// </summary>
    public static IServiceCollection AddSkopkaChatServerTrustedPostgreSql(this IServiceCollection services, Action<ServerTrustedChatPostgreSqlOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ServerTrustedChatPostgreSqlOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton(options.Chat);
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContextFactory<ServerTrustedChatDbContext>(builder => ServerTrustedChatDbContext.Configure(builder, options.ConnectionString, options.Schema));
        services.AddSingleton<PostgreSqlServerTrustedChatStore>();
        services.AddSingleton<IServerTrustedChatStore>(provider => provider.GetRequiredService<PostgreSqlServerTrustedChatStore>());
        services.AddSingleton<PostgreSqlServerTrustedChatEventOutbox>();
        services.AddSingleton<InProcessServerTrustedChatSignalHub>();
        services.AddSingleton<IServerTrustedChatSignalSink>(provider => provider.GetRequiredService<InProcessServerTrustedChatSignalHub>());
        services.AddSingleton<IServerTrustedChatLiveSignals>(provider => provider.GetRequiredService<InProcessServerTrustedChatSignalHub>());
        services.AddScoped(provider => new ServerTrustedChatService(
            provider.GetRequiredService<IServerTrustedChatStore>(),
            provider.GetRequiredService<IServerTrustedChatAccessPolicy>(),
            provider.GetRequiredService<IServerTrustedChatRateLimiter>(),
            options.Chat,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IServerTrustedChatSignalSink>(),
            provider.GetService<IServerTrustedAttachmentValidator>() is null ? null : provider.GetService<IAttachmentStore>(),
            provider.GetService<IAttachmentStore>() is null ? null : provider.GetService<IServerTrustedAttachmentValidator>()));
        if (options.EnableNotifyBridge)
        {
            services.AddHostedService<PostgreSqlServerTrustedChatSignalBridge>();
        }

        if (options.EnableMaintenance)
        {
            services.AddHostedService<ServerTrustedChatMaintenanceWorker>();
        }

        return services;
    }

    /// <summary>Registers the host event publisher and the hosted dispatcher over the server-trusted outbox.</summary>
    public static IServiceCollection AddSkopkaChatServerTrustedEventDispatch<TPublisher>(this IServiceCollection services)
        where TPublisher : class, IServerTrustedChatEventPublisher
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IServerTrustedChatEventPublisher, TPublisher>();
        services.AddHostedService<ServerTrustedChatEventDispatchWorker>();
        return services;
    }
}

internal sealed partial class ServerTrustedChatEventDispatchWorker(
    PostgreSqlServerTrustedChatEventOutbox outbox,
    IServerTrustedChatEventPublisher publisher,
    ServerTrustedChatPostgreSqlOptions options,
    TimeProvider time,
    ILogger<ServerTrustedChatEventDispatchWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner = $"skopka-trusted-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dispatcher = new ChatServerEventDispatcher(outbox, new PublisherAdapter(publisher), time, options.Dispatch);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await dispatcher.DispatchBatchAsync(_leaseOwner, stoppingToken).ConfigureAwait(false);
                if (result.Claimed == 0)
                {
                    await Task.Delay(options.Dispatch.IdleDelay, time, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                LogDispatchFailed(logger);
                await Task.Delay(options.Dispatch.InitialBackoff, time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server-trusted chat event dispatch failed; retrying.")]
    private static partial void LogDispatchFailed(ILogger logger);

    private sealed class PublisherAdapter(IServerTrustedChatEventPublisher inner) : IChatServerEventPublisher
    {
        public ValueTask PublishAsync(ChatServerOutboxMessage message, CancellationToken cancellationToken = default) => inner.PublishAsync(message, cancellationToken);
    }
}

internal sealed partial class ServerTrustedChatMaintenanceWorker(
    IServiceScopeFactory scopes,
    PostgreSqlServerTrustedChatEventOutbox outbox,
    ServerTrustedChatPostgreSqlOptions options,
    TimeProvider time,
    ILogger<ServerTrustedChatMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.MaintenanceInterval, time, stoppingToken).ConfigureAwait(false);
                await using (var scope = scopes.CreateAsyncScope())
                {
                    await scope.ServiceProvider.GetRequiredService<ServerTrustedChatService>().RunMaintenanceAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                }

                await outbox.DeletePublishedBeforeAsync(time.GetUtcNow() - options.Dispatch.PublishedRetention, 1000, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                LogMaintenanceFailed(logger);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server-trusted chat maintenance failed; it will run again.")]
    private static partial void LogMaintenanceFailed(ILogger logger);
}

/// <summary>
/// Listens on the schema's NOTIFY channel on one dedicated connection and republishes content-free signals into the
/// in-process hub. Notifications missed while reconnecting are repaired by component catch-up.
/// </summary>
internal sealed partial class PostgreSqlServerTrustedChatSignalBridge(
    ServerTrustedChatPostgreSqlOptions options,
    IServerTrustedChatSignalSink sink,
    TimeProvider time,
    ILogger<PostgreSqlServerTrustedChatSignalBridge> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = ServerTrustedChatSchema.ChannelFor(options.Schema);
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString) { Pooling = false, KeepAlive = 30 };
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
                connection.Notification += (_, notification) =>
                {
                    if (notification.Channel == channel && ServerTrustedChatSignalCodec.TryParse(notification.Payload, out var signal))
                    {
                        sink.Publish(signal);
                    }
                };
                await using (var command = new NpgsqlCommand($"LISTEN \"{channel}\"", connection))
                {
                    await command.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                }

                delay = TimeSpan.FromSeconds(1);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                LogBridgeFailed(logger);
                await Task.Delay(delay, time, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server-trusted chat NOTIFY bridge disconnected; reconnecting.")]
    private static partial void LogBridgeFailed(ILogger logger);
}

/// <summary>Strict parser of the bounded NOTIFY payloads written by the PostgreSQL store.</summary>
internal static class ServerTrustedChatSignalCodec
{
    public static bool TryParse(string? payload, out ServerTrustedChatSignal signal)
    {
        signal = default;
        if (payload is null || payload.Length > 128)
        {
            return false;
        }

        var parts = payload.Split('|');
        if (parts.Length < 3 || !Guid.TryParseExact(parts[1], "D", out var conversation) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
        {
            return false;
        }

        var id = new TrustedConversationId(conversation);
        switch (parts[0])
        {
            case "M" when parts.Length == 3:
                signal = new ServerTrustedChatSignal(id, ServerTrustedChatSignalKind.MessagesChanged, revision);
                return true;
            case "C" when parts.Length == 3:
                signal = new ServerTrustedChatSignal(id, ServerTrustedChatSignalKind.ConversationChanged, revision);
                return true;
            case "R" when parts.Length == 5 && Guid.TryParseExact(parts[3], "D", out var user) &&
                          long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var through):
                signal = new ServerTrustedChatSignal(id, ServerTrustedChatSignalKind.ReadMarkerChanged, revision, user, through);
                return true;
            default:
                return false;
        }
    }
}
