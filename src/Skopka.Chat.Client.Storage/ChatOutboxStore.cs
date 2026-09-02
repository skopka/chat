using Skopka.Chat.Client;

namespace Skopka.Chat.Client.Storage;

/// <summary>Durable encrypted fan-out outbox with bounded restart recovery and retention cleanup.</summary>
/// <remarks>
/// Plans contain recipient-specific ciphertext and a content hash, not plaintext. Implementations that add a
/// plaintext local echo enter the same local plaintext boundary as <see cref="IChatEventStore"/>.
/// </remarks>
public interface IChatOutboxStore : IChatFanOutPlanStore
{
    /// <summary>Reads a bounded stable batch of incomplete encrypted plans for restart recovery.</summary>
    IAsyncEnumerable<ChatFanOutPlan> ReadPendingAsync(
        int maximumCount = 50,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes at most a bounded number of completed plans older than the retention cutoff.</summary>
    ValueTask<int> DeleteCompletedBeforeAsync(
        DateTimeOffset cutoff,
        int maximumCount = 100,
        CancellationToken cancellationToken = default);
}

/// <summary>Bounded restart-dispatch result for encrypted outbox plans.</summary>
public sealed record ChatOutboxDispatchResult(
    int PlansVisited,
    int EnvelopesAccepted,
    int PlansCompleted);

/// <summary>Resumes stored ciphertext plans after restart without needing message plaintext.</summary>
public sealed class ChatOutboxDispatcher : IDisposable
{
    private readonly IChatOutboxStore _outbox;
    private readonly IChatTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Exception, bool> _isExpectedFailure;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates a serialized dispatcher over one session's transport and outbox.</summary>
    public ChatOutboxDispatcher(
        IChatOutboxStore outbox,
        IChatTransport transport,
        TimeProvider? timeProvider = null,
        Func<Exception, bool>? isExpectedFailure = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isExpectedFailure = isExpectedFailure ?? (static exception =>
            exception is HttpRequestException or TimeoutException);
    }

    /// <summary>Attempts a bounded pending batch, preserving exact message IDs and ciphertext.</summary>
    public async ValueTask<ChatOutboxDispatchResult> DispatchAsync(
        int maximumPlans = 50,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maximumPlans is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPlans));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var visited = 0;
            var accepted = 0;
            var completed = 0;
            await foreach (var plan in _outbox.ReadPendingAsync(maximumPlans, cancellationToken).ConfigureAwait(false))
            {
                visited++;
                var planFailed = false;
                foreach (var item in plan.Envelopes)
                {
                    if (item.IsAccepted)
                    {
                        continue;
                    }

                    try
                    {
                        await _transport.SendAsync(item.Envelope, cancellationToken).ConfigureAwait(false);
                        await _outbox.MarkAcceptedAsync(
                            plan.ConversationId,
                            plan.ContentId,
                            item.Envelope.MessageId,
                            _timeProvider.GetUtcNow(),
                            cancellationToken).ConfigureAwait(false);
                        accepted++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (_isExpectedFailure(exception))
                    {
                        planFailed = true;
                        break;
                    }
                }

                if (!planFailed)
                {
                    await _outbox.MarkCompletedAsync(
                        plan.ConversationId,
                        plan.ContentId,
                        _timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    completed++;
                }
            }

            return new ChatOutboxDispatchResult(visited, accepted, completed);
        }
        finally
        {
            _gate.Release();
        }
    }

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
}
