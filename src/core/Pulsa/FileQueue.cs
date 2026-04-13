using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Pulsa;

public class FileQueue
{
    private readonly Channel<QueueItem> _channel =
        Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<(string FilePath, int TaskIndex), byte> _pending =
        new(new CompositeKeyComparer());

    public void Enqueue(string filePath, int taskIndex)
    {
        var key = (filePath, taskIndex);
        if (_pending.TryAdd(key, 0))
            _channel.Writer.TryWrite(new QueueItem(filePath, taskIndex));
    }

    public void Complete(string filePath, int taskIndex) =>
        _pending.TryRemove((filePath, taskIndex), out _);

    public bool Contains(string filePath, int taskIndex) =>
        _pending.ContainsKey((filePath, taskIndex));

    public ChannelReader<QueueItem> Reader => _channel.Reader;

    private sealed class CompositeKeyComparer : IEqualityComparer<(string FilePath, int TaskIndex)>
    {
        public bool Equals((string FilePath, int TaskIndex) x, (string FilePath, int TaskIndex) y) =>
            x.TaskIndex == y.TaskIndex
            && string.Equals(x.FilePath, y.FilePath, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string FilePath, int TaskIndex) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FilePath),
                obj.TaskIndex);
    }
}
