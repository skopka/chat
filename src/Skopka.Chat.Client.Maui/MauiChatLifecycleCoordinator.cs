using Skopka.Chat.Client.Storage;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Maui;

/// <summary>Bounded foreground synchronization retry policy.</summary>
public sealed class MauiChatLifecycleOptions
{
    /// <summary>Delivery batch used by each foreground synchronization.</summary>
    public int MaximumDeliveryBatch { get; set; } = 50;

    /// <summary>Pending outbox plans attempted before polling.</summary>
    public int MaximumOutboxPlans { get; set; } = 50;

    /// <summary>Maximum attempts for an expected transient foreground failure.</summary>
    public int MaximumAttempts { get; set; } = 4;

    /// <summary>First exponential backoff delay.</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Largest exponential backoff delay before jitter.</summary>
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromSeconds(8);

    internal void Validate()
    {
        if (MaximumDeliveryBatch is < 1 or > ProtocolLimits.MaxDeliveryBatch ||
            MaximumOutboxPlans is < 1 or > 100 || MaximumAttempts is < 1 or > 10 ||
            InitialRetryDelay < TimeSpan.Zero || MaximumRetryDelay < InitialRetryDelay ||
            MaximumRetryDelay > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentException("The MAUI lifecycle synchronization options are invalid.");
        }
    }
}

/// <summary>
/// Runs replay once and serializes foreground/resume/manual synchronization for one authenticated session.
/// </summary>
/// <remarks>
/// This coordinator makes no background-execution guarantee. A future push notification should call
/// <see cref="RequestSynchronization"/> as a wake signal and must not carry message plaintext.
/// </remarks>
public sealed class MauiChatLifecycleCoordinator : IAsyncDisposable
{
    private readonly ChatSyncCoordinator _sync;
    private readonly ChatOutboxDispatcher? _outbox;
    private readonly MauiChatLifecycleOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _nextJitter;
    private readonly Func<Exception, bool> _isExpectedFailure;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _operationGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _activeOperation;
    private Task? _worker;
    private bool _disposed;

    /// <summary>Creates a lifecycle bridge that owns its sync coordinator and optional outbox dispatcher.</summary>
    public MauiChatLifecycleCoordinator(
        ChatSyncCoordinator sync,
        ChatOutboxDispatcher? outbox = null,
        MauiChatLifecycleOptions? options = null,
        TimeProvider? timeProvider = null,
        Func<double>? nextJitter = null,
        Func<Exception, bool>? isExpectedFailure = null)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _outbox = outbox;
        _options = options ?? new MauiChatLifecycleOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nextJitter = nextJitter ?? Random.Shared.NextDouble;
        _isExpectedFailure = isExpectedFailure ?? (static exception =>
            exception is HttpRequestException or TimeoutException);
    }

    /// <summary>Replays durable history, starts the foreground worker and requests the first sync.</summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_worker is null)
            {
                await _sync.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _worker = RunAsync(_lifetime.Token);
            }
        }
        finally
        {
            _startGate.Release();
        }

        RequestSynchronization();
    }

    /// <summary>Signals foreground/resume or a wake-only notification without starting parallel sync.</summary>
    public void RequestSynchronization()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_worker is null)
        {
            throw new InvalidOperationException("The MAUI lifecycle coordinator has not started.");
        }

        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    /// <summary>Cancels the active operation when the application sleeps.</summary>
    public void OnSleep()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_operationGate)
        {
            _activeOperation?.Cancel();
        }
    }

    /// <summary>Requests a new serialized operation when the application resumes.</summary>
    public void OnResume() => RequestSynchronization();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        lock (_operationGate)
        {
            _activeOperation?.Cancel();
        }

        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (_operationGate)
        {
            _activeOperation?.Dispose();
            _activeOperation = null;
        }

        _sync.Dispose();
        _outbox?.Dispose();
        _lifetime.Dispose();
        _wake.Dispose();
        _startGate.Dispose();
    }

    private async Task RunAsync(CancellationToken lifetimeToken)
    {
        while (true)
        {
            await _wake.WaitAsync(lifetimeToken).ConfigureAwait(false);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            lock (_operationGate)
            {
                _activeOperation = operation;
            }

            try
            {
                await SynchronizeWithRetryAsync(operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_operationGate)
                {
                    if (ReferenceEquals(_activeOperation, operation))
                    {
                        _activeOperation = null;
                    }
                }
            }
        }
    }

    private async Task SynchronizeWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.MaximumAttempts; attempt++)
        {
            try
            {
                if (_outbox is not null)
                {
                    await _outbox.DispatchAsync(_options.MaximumOutboxPlans, cancellationToken).ConfigureAwait(false);
                }

                await _sync.SynchronizeAsync(_options.MaximumDeliveryBatch, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (_isExpectedFailure(exception) && attempt + 1 < _options.MaximumAttempts)
            {
                var delay = GetDelay(attempt);
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (_isExpectedFailure(exception))
            {
                return;
            }
        }
    }

    private TimeSpan GetDelay(int attempt)
    {
        var exponentialTicks = Math.Min(
            _options.InitialRetryDelay.Ticks * (1L << attempt),
            _options.MaximumRetryDelay.Ticks);
        var jitter = _nextJitter();
        if (double.IsNaN(jitter) || jitter is < 0 or > 1)
        {
            jitter = 0.5;
        }

        return TimeSpan.FromTicks(checked((long)(exponentialTicks * (0.75 + jitter * 0.5))));
    }
}

/// <summary>Trusted server-token identity for exactly one MAUI client session.</summary>
public readonly record struct MauiChatSessionIdentity(UserId UserId, DeviceId DeviceId)
{
    /// <summary>Validates the host-resolved user/device pair.</summary>
    public void Validate()
    {
        if (UserId.Value == Guid.Empty || DeviceId.Value == Guid.Empty)
        {
            throw new ArgumentException("The MAUI chat session identity is invalid.");
        }
    }
}

/// <summary>Owns one authenticated user/device session and its scoped resources.</summary>
public sealed class MauiChatSession : IAsyncDisposable
{
    private readonly IReadOnlyList<IDisposable> _resources;
    private bool _disposed;

    /// <summary>Creates a session. Access-token acquisition remains host-provided.</summary>
    public MauiChatSession(
        MauiChatSessionIdentity identity,
        MauiChatLifecycleCoordinator lifecycle,
        IReadOnlyList<IDisposable>? resources = null)
    {
        identity.Validate();
        Identity = identity;
        Lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _resources = resources ?? [];
    }

    /// <summary>Authenticated user/device pair resolved by the host and server token claims.</summary>
    public MauiChatSessionIdentity Identity { get; }

    /// <summary>Lifecycle coordinator isolated to this session.</summary>
    public MauiChatLifecycleCoordinator Lifecycle { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Lifecycle.DisposeAsync().ConfigureAwait(false);
        foreach (var resource in _resources.Reverse())
        {
            resource.Dispose();
        }
    }
}

/// <summary>Serializes account switching and disposes the previous user/device session first.</summary>
public sealed class MauiChatSessionManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MauiChatSession? _current;
    private bool _disposed;

    /// <summary>Current session, or null after logout.</summary>
    public MauiChatSession? Current => _current;

    /// <summary>Installs a new isolated session after cancelling and disposing the old one.</summary>
    public async ValueTask SwitchAsync(
        MauiChatSession next,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(next);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _current;
            _current = null;
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            _current = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Cancels and disposes the current account session.</summary>
    public async ValueTask LogoutAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _current;
            _current = null;
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await LogoutAsync().ConfigureAwait(false);
        _disposed = true;
        _gate.Dispose();
    }
}
