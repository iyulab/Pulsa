using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Pulsa;

public class FileQueue
{
    private readonly Channel<string> _channel =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);

    public void Enqueue(string filePath)
    {
        if (_pending.TryAdd(filePath, 0))
            _channel.Writer.TryWrite(filePath);
    }

    public void Complete(string filePath) => _pending.TryRemove(filePath, out _);

    public bool Contains(string filePath) => _pending.ContainsKey(filePath);

    public ChannelReader<string> Reader => _channel.Reader;
}
