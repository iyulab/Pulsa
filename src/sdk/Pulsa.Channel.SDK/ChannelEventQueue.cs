namespace PulsaChannel;

/// <summary>
/// Keeps the most recent events in order and lets a consumer read them by cursor. A consumer that asks again with the last
/// <see cref="ChannelEvent.Seq"/> it saw gets what came after, so a lost reply loses nothing (at-least-once).
/// </summary>
public sealed class ChannelEventQueue : IChannelEventSink
{
    private readonly object gate = new();
    private readonly List<ChannelEvent> events = [];
    private readonly int capacity;
    private readonly TimeProvider time;
    private long last;
    private TaskCompletionSource arrival = NewArrival();

    public ChannelEventQueue(int capacity = 1000, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Names this running queue. A consumer that holds a cursor from another run presents the old epoch and is told to start over.</summary>
    public string Epoch { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The <see cref="ChannelEvent.Seq"/> of the oldest event still kept, 0 when there is none.</summary>
    public long Oldest
    {
        get { lock (gate) { return events.Count == 0 ? 0 : events[0].Seq; } }
    }

    /// <summary>The <see cref="ChannelEvent.Seq"/> of the newest event, 0 when there is none yet.</summary>
    public long Last
    {
        get { lock (gate) { return last; } }
    }

    public long Publish(ChannelEvent channelEvent)
    {
        ArgumentNullException.ThrowIfNull(channelEvent);
        TaskCompletionSource toWake;
        long seq;
        lock (gate)
        {
            last++;
            seq = last;
            events.Add(channelEvent with { Seq = last, At = time.GetUtcNow() });
            if (events.Count > capacity) events.RemoveAt(0);
            toWake = arrival;
            arrival = NewArrival();
        }

        toWake.TrySetResult();
        return seq;
    }

    /// <summary>
    /// The events after <paramref name="after"/> (at most <paramref name="max"/>), waiting up to <paramref name="timeout"/> for the
    /// first one when there are none yet.
    /// </summary>
    public async Task<IReadOnlyList<ChannelEvent>> PollAsync(long after, int max, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero) deadline.CancelAfter(timeout);
        else deadline.Cancel();

        while (true)
        {
            Task wait;
            lock (gate)
            {
                var found = events.Where(e => e.Seq > after).Take(max).ToList();
                if (found.Count > 0) return found;
                wait = arrival.Task;
            }

            try
            {
                await wait.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return [];
            }
        }
    }

    private static TaskCompletionSource NewArrival() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
