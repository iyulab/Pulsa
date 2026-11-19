using System.Text.Json.Nodes;

namespace PulsaChannel.Cli;

/// <summary>
/// The stdio transport: newline-delimited JSON-RPC from a reader to a writer. Every request runs on its own task so a long poll does
/// not hold up a ping.
/// </summary>
public sealed class McpStdioServer(McpHandler handler, TextReader input, TextWriter output)
{
    private readonly SemaphoreSlim writing = new(1, 1);

    public McpStdioServer(ChannelConnector connector, TextReader input, TextWriter output)
        : this(new McpHandler(connector), input, output)
    {
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var running = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            running.Add(Task.Run(() => AnswerAsync(line, cancellationToken), CancellationToken.None));
            running.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task AnswerAsync(string line, CancellationToken cancellationToken)
    {
        JsonObject? reply;
        try
        {
            reply = await handler.HandleAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (reply is null) return;
        await writing.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(reply.ToJsonString()).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            writing.Release();
        }
    }
}
