using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Browser;

/// <summary>Encrypted immutable fan-out plans with atomic acceptance updates and bounded restart enumeration.</summary>
public sealed class BrowserChatOutboxStore(BrowserVault vault) : IChatOutboxStore
{
    private readonly BrowserVault _vault = vault ?? throw new ArgumentNullException(nameof(vault));

    /// <inheritdoc />
    public async ValueTask<ChatFanOutPlan?> LoadAsync(ConversationId conversationId, ChatContentId contentId, CancellationToken cancellationToken = default)
    {
        var key = Key(conversationId, contentId);
        var row = await _vault.ReadAsync("plans", key, cancellationToken).ConfigureAwait(false);
        return row.Status == "absent" ? null : Decode(row, key);
    }

    /// <inheritdoc />
    public async ValueTask<ChatFanOutPlanStoreResult> StoreAsync(ChatFanOutPlan plan, CancellationToken cancellationToken = default)
    {
        ValidateOwner(plan);
        var bytes = BrowserStoreEncoding.Encode(BrowserPlanRecord.FromDomain(plan), BrowserStoreJson.Default.BrowserPlanRecord);
        try
        {
            if (await _vault.WriteAsync("plans", Key(plan.ConversationId, plan.ContentId), "pending", bytes, null, cancellationToken).ConfigureAwait(false))
            { return ChatFanOutPlanStoreResult.Stored; }
            var existing = await LoadAsync(plan.ConversationId, plan.ContentId, cancellationToken).ConfigureAwait(false);
            return existing is not null && Equivalent(existing, plan) ? ChatFanOutPlanStoreResult.Duplicate : ChatFanOutPlanStoreResult.Conflict;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <inheritdoc />
    public ValueTask MarkAcceptedAsync(ConversationId conversationId, ChatContentId contentId, MessageId messageId,
        DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) =>
        UpdateAsync(conversationId, contentId, messageId, acceptedAt, false, cancellationToken);

    /// <inheritdoc />
    public ValueTask MarkCompletedAsync(ConversationId conversationId, ChatContentId contentId, DateTimeOffset completedAt,
        CancellationToken cancellationToken = default) => UpdateAsync(conversationId, contentId, default, completedAt, true, cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatFanOutPlan> ReadPendingAsync(int maximumCount = 50, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 100) { throw new ArgumentOutOfRangeException(nameof(maximumCount)); }
        var rows = await _vault.PageAsync("plans", "pending", 0, 0, maximumCount, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var stored = await _vault.ReadAsync("plans", row.Key, cancellationToken).ConfigureAwait(false);
            var plan = Decode(stored, row.Key);
            if (plan.CompletedAt is null) { yield return plan; }
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteCompletedBeforeAsync(DateTimeOffset cutoff, int maximumCount = 100, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 100) { throw new ArgumentOutOfRangeException(nameof(maximumCount)); }
        var rows = await _vault.PageAsync("plans", "completed", 0, 0, maximumCount, cancellationToken).ConfigureAwait(false);
        var removed = 0;
        foreach (var row in rows)
        {
            var stored = await _vault.ReadAsync("plans", row.Key, cancellationToken).ConfigureAwait(false);
            var plan = Decode(stored, row.Key);
            if (plan.CompletedAt < cutoff)
            {
                await _vault.RemoveAsync("plans", row.Key, stored.Revision, cancellationToken).ConfigureAwait(false);
                removed++;
            }
        }
        return removed;
    }

    private async ValueTask UpdateAsync(ConversationId conversationId, ChatContentId contentId, MessageId messageId,
        DateTimeOffset at, bool complete, CancellationToken cancellationToken)
    {
        var key = Key(conversationId, contentId);
        await using var lease = await _vault.AcquireAsync("plan-" + key, cancellationToken).ConfigureAwait(false);
        var row = await _vault.ReadAsync("plans", key, cancellationToken).ConfigureAwait(false);
        var plan = Decode(row, key);
        if (at == default || complete && plan.Envelopes.Any(item => !item.IsAccepted) ||
            !complete && !plan.Envelopes.Any(item => item.Envelope.MessageId == messageId))
        { throw new BrowserStorageException("corrupt"); }
        var changed = new ChatFanOutPlan(plan.ConversationId, plan.ContentId, plan.SenderUserId, plan.SenderDeviceId,
            plan.LocalEchoMessageId, plan.SentAt, plan.ContentHash.Span,
            plan.Envelopes.Select(item => item.Envelope.MessageId == messageId ? item with { IsAccepted = true } : item).ToArray(),
            complete ? plan.CompletedAt ?? at : plan.CompletedAt);
        var bytes = BrowserStoreEncoding.Encode(BrowserPlanRecord.FromDomain(changed), BrowserStoreJson.Default.BrowserPlanRecord);
        try
        {
            if (!await _vault.WriteAsync("plans", key, changed.CompletedAt is null ? "pending" : "completed", bytes, row.Revision, cancellationToken).ConfigureAwait(false))
            { throw new BrowserStorageException("conflict"); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private ChatFanOutPlan Decode(VaultResult row, string key)
    {
        try
        {
            var plan = BrowserStoreEncoding.Decode(row.Data, BrowserStoreJson.Default.BrowserPlanRecord).ToDomain();
            ValidateOwner(plan);
            if (Key(plan.ConversationId, plan.ContentId) != key || row.Partition != (plan.CompletedAt is null ? "pending" : "completed"))
            { throw new BrowserStorageException("corrupt"); }
            return plan;
        }
        catch (Exception error) when (error is ArgumentException or FormatException) { throw new BrowserStorageException("corrupt"); }
    }
    private void ValidateOwner(ChatFanOutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SenderUserId != _vault.Scope.UserId) { throw new BrowserStorageException("corrupt"); }
    }
    private static string Key(ConversationId conversationId, ChatContentId contentId) =>
        BrowserStoreEncoding.Id(conversationId.Value) + "-" + BrowserStoreEncoding.Id(contentId.Value);
    private static bool Equivalent(ChatFanOutPlan left, ChatFanOutPlan right) =>
        left.SenderUserId == right.SenderUserId && left.SenderDeviceId == right.SenderDeviceId &&
        left.LocalEchoMessageId == right.LocalEchoMessageId && left.SentAt == right.SentAt &&
        left.ContentHash.Span.SequenceEqual(right.ContentHash.Span) && left.Envelopes.Count == right.Envelopes.Count &&
        left.Envelopes.Zip(right.Envelopes).All(pair => CanonicalEnvelopeEncoding.EncodeEnvelope(pair.First.Envelope)
            .AsSpan().SequenceEqual(CanonicalEnvelopeEncoding.EncodeEnvelope(pair.Second.Envelope)));
}
