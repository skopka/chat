using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Storage;

/// <summary>Result of applying one bounded local-history page.</summary>
public sealed record ChatHistoryPageResult(int Applied, bool HasOlder);

/// <summary>Serializes newest-first cursor paging and applies each returned page chronologically.</summary>
public sealed class ChatHistoryPager : IDisposable
{
    private readonly IPagedChatEventStore _store;
    private readonly IChatEventApplier _applier;
    private readonly ConversationId _conversationId;
    private readonly int _pageSize;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _previousCursor;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Creates a bounded pager for one conversation projection.</summary>
    public ChatHistoryPager(
        IPagedChatEventStore store,
        IChatEventApplier applier,
        ConversationId conversationId,
        int pageSize = 50)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        if (pageSize is < 1 or > ChatEventPagingLimits.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        _conversationId = conversationId;
        _pageSize = pageSize;
    }

    /// <summary>Whether the last applied page exposed a preceding cursor.</summary>
    public bool HasOlder => _initialized && _previousCursor is not null;

    /// <summary>Loads and applies the newest page exactly once.</summary>
    public ValueTask<ChatHistoryPageResult> LoadInitialAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(initial: true, cancellationToken);

    /// <summary>Loads and applies the preceding page, or returns zero when history is exhausted.</summary>
    public ValueTask<ChatHistoryPageResult> LoadPreviousAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(initial: false, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private async ValueTask<ChatHistoryPageResult> LoadAsync(bool initial, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initial && _initialized)
            {
                return new ChatHistoryPageResult(0, _previousCursor is not null);
            }

            if (!initial && (!_initialized || _previousCursor is null))
            {
                return new ChatHistoryPageResult(0, false);
            }

            var page = await _store.ReadPreviousPageAsync(
                _conversationId,
                initial ? null : _previousCursor,
                _pageSize,
                cancellationToken).ConfigureAwait(false);
            if (page.Items.Count > _pageSize || page.Items.Any(item => item.ConversationId != _conversationId))
            {
                throw new ChatEventStorageException("The local chat history page was invalid.");
            }

            foreach (var item in page.Items)
            {
                await _applier.ApplyAsync(item, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
            _previousCursor = page.PreviousCursor;
            return new ChatHistoryPageResult(page.Items.Count, _previousCursor is not null);
        }
        finally
        {
            _gate.Release();
        }
    }
}
