using System.Globalization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Skopka.Chat.ServerTrusted.Blazor;

/// <summary>
/// Total unread count over the user's readable server-trusted conversations. Renders nothing when zero, refreshes on
/// live signals (coalesced to at most one read per second) and periodically. Prerender-safe: no JavaScript.
/// </summary>
public sealed partial class ServerTrustedChatUnreadBadge : ComponentBase, IAsyncDisposable
{
    [Inject]
    private IServiceProvider Services { get; set; } = null!;

    [Inject]
    private IServiceScopeFactory ScopeFactory { get; set; } = null!;


    private static readonly TimeSpan MinimumRefreshSpacing = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _lifetime;
    private IServerTrustedChatOperationRunner? _runner;
    private Guid _loadedUserId;
    private volatile int _total;
    private bool _disposed;

    /// <summary>
    /// Authenticated user. Trustworthy only because server-rendered component descriptors are protected by Data
    /// Protection; never take it from a query string or a WebAssembly component.
    /// </summary>
    [Parameter, EditorRequired]
    public Guid UserId { get; set; }

    /// <summary>Optional class for theme scoping.</summary>
    [Parameter]
    public string? CssClass { get; set; }

    /// <summary>Replaceable localized strings; defaults to <see cref="ServerTrustedChatStrings.Default"/>.</summary>
    [Parameter]
    public ServerTrustedChatStrings? Strings { get; set; }

    /// <summary>Periodic refresh that repairs lost live signals (default 30 seconds, minimum 1 second).</summary>
    [Parameter]
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    private ServerTrustedChatStrings S => Strings ?? ServerTrustedChatStrings.Default;

    private string RootClass => string.IsNullOrWhiteSpace(CssClass) ? "skopka-chat-unread" : $"skopka-chat-unread {CssClass}";

    private string DisplayText => _total > 99 ? S.UnreadOverflow : _total.ToString(CultureInfo.CurrentCulture);

    private string AccessibleLabel => string.Create(CultureInfo.CurrentCulture, $"{S.UnreadMessages}: {DisplayText}");

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (UserId == Guid.Empty)
        {
            throw new ArgumentException("The user identifier is required.");
        }

        if (RefreshInterval < MinimumRefreshSpacing || RefreshInterval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(RefreshInterval));
        }

        _runner ??= Services.GetService<IServerTrustedChatOperationRunner>() ?? new ScopedServerTrustedChatOperationRunner(ScopeFactory);
        if (_loadedUserId == UserId)
        {
            return;
        }

        var restart = _lifetime is not null;
        _lifetime?.Cancel();
        _lifetime = new CancellationTokenSource();
        _loadedUserId = UserId;
        _total = 0;
        await RefreshAsync(UserId, _lifetime.Token);
        if (restart && RendererInfo.IsInteractive)
        {
            StartLive(UserId, _lifetime.Token);
        }
    }

    /// <inheritdoc />
    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender && RendererInfo.IsInteractive && !_disposed && _lifetime is not null)
        {
            StartLive(UserId, _lifetime.Token);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _lifetime?.Cancel();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void StartLive(Guid userId, CancellationToken cancellationToken)
    {
        var requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        _ = RunRefreshesAsync(userId, requests.Reader, cancellationToken);
        _ = RunTimerAsync(requests.Writer, cancellationToken);
        if (Services.GetService<IServerTrustedChatLiveSignals>() is { } signals)
        {
            _ = RunSignalsAsync(signals, userId, requests.Writer, cancellationToken);
        }

        // Covers anything that changed between prerender and the interactive start.
        requests.Writer.TryWrite(true);
    }

    private async Task<bool> RefreshAsync(Guid userId, CancellationToken cancellationToken)
    {
        int total;
        try
        {
            total = await _runner!.RunAsync((service, token) => service.GetUnreadTotalAsync(userId, token), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
#pragma warning disable CA1031 // A transient store failure keeps the last known count; the next refresh repairs it.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var changed = total != _total;
        _total = Math.Max(0, total);
        return changed;
    }

    private async Task RunRefreshesAsync(Guid userId, ChannelReader<bool> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                reader.TryRead(out _);
                if (await RefreshAsync(userId, cancellationToken).ConfigureAwait(false) && !_disposed)
                {
                    try
                    {
                        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }

                // Coalesce bursts: at most one refresh per second.
                await Task.Delay(MinimumRefreshSpacing, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunTimerAsync(ChannelWriter<bool> writer, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                writer.TryWrite(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSignalsAsync(
        IServerTrustedChatLiveSignals signals,
        Guid userId,
        ChannelWriter<bool> writer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var signal in signals.SubscribeAsync(null, cancellationToken).ConfigureAwait(false))
                {
                    // Another user's read marker never changes this user's unread total.
                    if (signal.Kind == ServerTrustedChatSignalKind.ReadMarkerChanged && signal.UserId != userId)
                    {
                        continue;
                    }

                    writer.TryWrite(true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failing signal source must not end the circuit; the periodic refresh continues.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            try
            {
                await Task.Delay(RefreshInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
