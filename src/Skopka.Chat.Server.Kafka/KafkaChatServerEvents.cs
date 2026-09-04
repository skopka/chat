using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Skopka.Chat.Server.Kafka;

/// <summary>Kafka topic names owned by the first server-event vertical slice.</summary>
public static class KafkaChatServerEventTopics
{
    /// <summary>Version-one metadata notification for a committed encrypted envelope.</summary>
    public const string EncryptedEnvelopeAcceptedV1 = "skopka.chat.encrypted-envelope-accepted.v1";
}

/// <summary>Kafka producer and hosted dispatcher settings.</summary>
public sealed class KafkaChatServerEventOptions
{
    /// <summary>Comma-separated Kafka bootstrap servers. Local development normally uses kafka:9092.</summary>
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>Pre-created topic for encrypted-envelope acceptance v1.</summary>
    public string EncryptedEnvelopeAcceptedTopic { get; set; } = KafkaChatServerEventTopics.EncryptedEnvelopeAcceptedV1;

    /// <summary>Bounded Kafka client identifier.</summary>
    public string ClientId { get; set; } = "skopka-chat-server-events";

    /// <summary>Maximum time for broker acknowledgement of one produce request.</summary>
    public TimeSpan DeliveryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Durable claim, retry, and idle-loop policy.</summary>
    public ChatServerEventDispatchOptions Dispatch { get; } = new();

    internal void Validate()
    {
        Dispatch.Validate();
        if (string.IsNullOrWhiteSpace(BootstrapServers) || BootstrapServers.Length > 4096 ||
            !ValidTopic(EncryptedEnvelopeAcceptedTopic) ||
            string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 128 ||
            DeliveryTimeout < TimeSpan.FromSeconds(1) || DeliveryTimeout > TimeSpan.FromMinutes(5) ||
            Dispatch.LeaseDuration <= DeliveryTimeout + TimeSpan.FromSeconds(5))
        {
            throw new ArgumentException("The Kafka server event options are invalid.");
        }
    }

    private static bool ValidTopic(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 249 &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}

/// <summary>Dependency-injection registration for the optional Kafka event adapter.</summary>
public static class KafkaChatServerEventServiceCollectionExtensions
{
    /// <summary>
    /// Adds an idempotent Kafka producer and controlled hosted dispatcher. The host must also register a scoped
    /// <see cref="IChatServerEventOutbox"/>, normally <c>PostgreSqlChatEventOutbox</c>.
    /// </summary>
    public static IServiceCollection AddSkopkaChatKafkaServerEvents(
        this IServiceCollection services,
        Action<KafkaChatServerEventOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new KafkaChatServerEventOptions();
        configure(options);
        options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IChatServerEventPublisher, KafkaChatServerEventPublisher>();
        services.AddHostedService<KafkaChatServerEventWorker>();
        return services;
    }
}

/// <summary>Publishes exact versioned outbox bytes to a pre-created Kafka topic.</summary>
public sealed class KafkaChatServerEventPublisher : IChatServerEventPublisher, IDisposable
{
    private static readonly byte[] JsonContentType = "application/json"u8.ToArray();
    private readonly KafkaChatServerEventOptions _options;
    private readonly IProducer<string, byte[]> _producer;
    private bool _disposed;

    /// <summary>Creates a reliable producer with broker idempotence and topic auto-creation disabled.</summary>
    public KafkaChatServerEventPublisher(KafkaChatServerEventOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _producer = new ProducerBuilder<string, byte[]>(CreateProducerConfig(options)).Build();
    }

    internal static ProducerConfig CreateProducerConfig(KafkaChatServerEventOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = options.ClientId,
            AllowAutoCreateTopics = false,
            EnableIdempotence = true,
            Acks = Acks.All,
            MaxInFlight = 5,
            MessageSendMaxRetries = int.MaxValue,
            MessageTimeoutMs = checked((int)options.DeliveryTimeout.TotalMilliseconds)
        };
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(
        ChatServerOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = CreatePublishRequest(_options, message);
        var result = await _producer.ProduceAsync(
            request.Topic,
            request.Message,
            cancellationToken).ConfigureAwait(false);
        if (result.Status != PersistenceStatus.Persisted)
        {
            throw new InvalidOperationException("The Kafka broker did not persist the server event.");
        }
    }

    internal static KafkaPublishRequest CreatePublishRequest(
        KafkaChatServerEventOptions options,
        ChatServerOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ArgumentNullException.ThrowIfNull(message);
        if (message.EventType != ChatServerEventTypes.EncryptedEnvelopeAccepted ||
            message.EventVersion != ChatServerEventTypes.EncryptedEnvelopeAcceptedVersion)
        {
            throw new ArgumentException("The Kafka server event type is not supported.", nameof(message));
        }

        var headers = new Headers
        {
            { "content-type", JsonContentType },
            { "skopka-event-id", Encoding.UTF8.GetBytes(message.EventId.ToString("D")) },
            { "skopka-event-type", Encoding.UTF8.GetBytes(message.EventType) },
            { "skopka-event-version", Encoding.ASCII.GetBytes(message.EventVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)) }
        };
        return new KafkaPublishRequest(
            options.EncryptedEnvelopeAcceptedTopic,
            new Message<string, byte[]>
            {
                Key = message.PartitionKey,
                Value = message.Payload.ToArray(),
                Headers = headers,
                Timestamp = new Timestamp(message.OccurredAt)
            });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}

internal sealed record KafkaPublishRequest(string Topic, Message<string, byte[]> Message);

internal sealed class KafkaChatServerEventWorker(
    IServiceScopeFactory scopeFactory,
    IChatServerEventPublisher publisher,
    TimeProvider timeProvider,
    KafkaChatServerEventOptions options,
    ILogger<KafkaChatServerEventWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, Exception?> LogRescheduled = LoggerMessage.Define<int>(
        LogLevel.Warning,
        new EventId(1, "KafkaServerEventsRescheduled"),
        "Kafka server event delivery rescheduled {EventCount} event(s); payload and broker errors are intentionally omitted.");
    private static readonly Action<ILogger, Exception?> LogUnavailable = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2, "KafkaServerEventsUnavailable"),
        "Kafka server event dispatch is unavailable; diagnostic details are intentionally omitted.");
    private readonly string _leaseOwner = Guid.NewGuid().ToString("N");
    private DateTimeOffset _nextCleanupAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var outbox = scope.ServiceProvider.GetRequiredService<IChatServerEventOutbox>();
                var dispatcher = new ChatServerEventDispatcher(outbox, publisher, timeProvider, options.Dispatch);
                var result = await dispatcher.DispatchBatchAsync(_leaseOwner, stoppingToken).ConfigureAwait(false);
                var now = timeProvider.GetUtcNow();
                if (now >= _nextCleanupAt)
                {
                    await outbox.DeletePublishedBeforeAsync(
                        now - options.Dispatch.PublishedRetention,
                        options.Dispatch.BatchSize,
                        stoppingToken).ConfigureAwait(false);
                    _nextCleanupAt = now + TimeSpan.FromHours(1);
                }

                if (result.Rescheduled > 0)
                {
                    LogRescheduled(logger, result.Rescheduled, null);
                }

                if (result.Claimed == 0)
                {
                    await Task.Delay(options.Dispatch.IdleDelay, timeProvider, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                LogUnavailable(logger, null);
                await Task.Delay(options.Dispatch.InitialBackoff, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
