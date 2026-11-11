using Microsoft.Extensions.DependencyInjection;

namespace Skopka.Chat.ServerTrusted.Blazor;

/// <summary>
/// Runs one engine operation. The default implementation opens a fresh service scope per operation, because the
/// engine and its stores are scoped while Blazor circuits are long-lived.
/// </summary>
public interface IServerTrustedChatOperationRunner
{
    /// <summary>Runs <paramref name="operation"/> against an engine instance and returns its result.</summary>
    ValueTask<TResult> RunAsync<TResult>(
        Func<ServerTrustedChatService, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves <see cref="ServerTrustedChatService"/> from a new asynchronous service scope for every operation.</summary>
public sealed class ScopedServerTrustedChatOperationRunner : IServerTrustedChatOperationRunner
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Creates a runner over the host's root scope factory.</summary>
    public ScopedServerTrustedChatOperationRunner(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public async ValueTask<TResult> RunAsync<TResult>(
        Func<ServerTrustedChatService, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var scope = _scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var service = scope.ServiceProvider.GetRequiredService<ServerTrustedChatService>();
            return await operation(service, cancellationToken).ConfigureAwait(false);
        }
    }
}
