using System.Text.Json;
using System.Text.Json.Nodes;
using PulsaChannel;
using PulsaChannel.Cli;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Usage();
    return args.Length == 0 ? 2 : 0;
}

var command = args[0];
var options = ParseFlags(args[1..], out var bad);
if (bad is not null)
{
    Console.Error.WriteLine(bad);
    return 2;
}

IChannelProvider provider;
IReadOnlyList<string> secrets;
bool startToSend;
try
{
    (provider, secrets, startToSend) = ProviderFactory.Create(options.GetValueOrDefault("provider"), Environment.GetEnvironmentVariable);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

await using var connector = new ChannelConnector(provider, secrets);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

switch (command)
{
    case "serve":
        {
            await connector.StartAsync(cancel.Token);
            var server = new McpStdioServer(connector, Console.In, Console.Out);
            await server.RunAsync(cancel.Token);
            await connector.StopAsync(CancellationToken.None);
            return 0;
        }

    case "send":
        {
            if (!options.TryGetValue("conversation", out var conversation) || !options.TryGetValue("text", out var text))
            {
                Usage();
                return 2;
            }

            if (startToSend) await connector.StartAsync(cancel.Token);
            Uri? link = options.TryGetValue("link", out var rawLink) && Uri.TryCreate(rawLink, UriKind.Absolute, out var parsed) ? parsed : null;
            var result = await connector.SendAsync(new ChannelSendRequest(conversation, text, link), cancel.Token);
            Console.WriteLine(ChannelJson.Send(result).ToJsonString());
            return result.Delivered ? 0 : 1;
        }

    case "status":
        {
            await connector.StartAsync(cancel.Token);
            // One look at the connection: give a first attempt up to ten seconds to settle.
            var until = DateTimeOffset.UtcNow.AddSeconds(10);
            while (connector.Status.State == ChannelState.Connecting && DateTimeOffset.UtcNow < until) await Task.Delay(100, cancel.Token);
            Console.WriteLine(ChannelJson.Status(connector.Status).ToJsonString());
            await connector.StopAsync(CancellationToken.None);
            return connector.Status.State == ChannelState.Connected ? 0 : 1;
        }

    case "listen":
        {
            var after = options.TryGetValue("after", out var rawAfter) && long.TryParse(rawAfter, out var a) ? a : 0;
            var seconds = options.TryGetValue("seconds", out var rawSeconds) && int.TryParse(rawSeconds, out var s) ? s : 0;
            if (seconds > 0) cancel.CancelAfter(TimeSpan.FromSeconds(seconds));
            await connector.StartAsync(cancel.Token);
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    foreach (var e in await connector.PollAsync(after, 100, TimeSpan.FromSeconds(30), cancel.Token))
                    {
                        Console.WriteLine(ChannelJson.Event(e).ToJsonString());
                        after = e.Seq;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // the time given, or Ctrl+C
            }

            await connector.StopAsync(CancellationToken.None);
            return 0;
        }

    default:
        Usage();
        return 2;
}

static Dictionary<string, string> ParseFlags(string[] args, out string? error)
{
    error = null;
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
        {
            error = $"Unexpected argument '{args[i]}'.";
            return options;
        }

        options[args[i][2..]] = args[++i];
    }

    return options;
}

static void Usage() => Console.Error.WriteLine("""
    Usage:
      PulsaChannel.Cli serve                                            Model Context Protocol server on stdin/stdout
      PulsaChannel.Cli send --conversation <id> --text <text> [--link <url>]
      PulsaChannel.Cli status
      PulsaChannel.Cli listen [--after <seq>] [--seconds <n>]
    Every command takes --provider fake|telegram (default: PULSA_CHANNEL_PROVIDER, else fake).
    Credentials come from the environment: PULSA_CHANNEL_TELEGRAM_TOKEN (and PULSA_CHANNEL_TELEGRAM_API to point at another Bot API root).
    Exit code: 0 done, 1 not delivered or not connected, 2 wrong usage.
    """);
