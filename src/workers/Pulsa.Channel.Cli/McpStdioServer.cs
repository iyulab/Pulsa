using System.Text.Json;
using System.Text.Json.Nodes;

namespace PulsaChannel.Cli;

/// <summary>
/// A Model Context Protocol server over newline-delimited JSON-RPC (the stdio transport) with three tools:
/// <c>channel_send</c>, <c>channel_status</c> and <c>channel_poll</c>. It implements only what a tools-only server needs
/// (initialize, ping, tools/list, tools/call); every request runs on its own task so a long poll does not hold up a ping.
/// </summary>
public sealed class McpStdioServer(ChannelConnector connector, TextReader input, TextWriter output)
{
    public const string ServerName = "pulsa-channel";
    public const string DefaultProtocolVersion = "2025-06-18";

    private readonly SemaphoreSlim writing = new(1, 1);

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
            running.Add(Task.Run(() => HandleAsync(line, cancellationToken), CancellationToken.None));
            running.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task HandleAsync(string line, CancellationToken cancellationToken)
    {
        JsonNode? id = null;
        try
        {
            var request = JsonNode.Parse(line) as JsonObject;
            if (request is null)
            {
                await ReplyErrorAsync(null, -32600, "Invalid request").ConfigureAwait(false);
                return;
            }

            id = request["id"]?.DeepClone();
            if (request["method"] is null) return; // a response from the client, which this server never asks for
            if (id is null) return; // a notification (notifications/initialized, notifications/cancelled) needs no answer
            if (request["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
            {
                await ReplyErrorAsync(id, -32600, "method must be a string.").ConfigureAwait(false);
                return;
            }

            var result = method switch
            {
                "initialize" => Initialize(request["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = Tools() },
                "tools/call" => await CallAsync(request["params"] as JsonObject, cancellationToken).ConfigureAwait(false),
                _ => null,
            };

            if (result is null) await ReplyErrorAsync(id, -32601, $"Method not found: {method}").ConfigureAwait(false);
            else await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await ReplyErrorAsync(null, -32700, "Parse error").ConfigureAwait(false);
        }
        catch (ArgumentException e)
        {
            await ReplyErrorAsync(id, -32602, e.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception)
        {
            // Whatever it was, the client is waiting for an answer to this id; what the exception says is not for the client.
            if (id is not null) await ReplyErrorAsync(id, -32603, "Internal error").ConfigureAwait(false);
        }
    }

    private static JsonObject Initialize(JsonObject? parameters) => new()
    {
        ["protocolVersion"] = parameters?["protocolVersion"] is JsonValue version && version.TryGetValue<string>(out var asked) ? asked : DefaultProtocolVersion,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "0.0.1" },
        ["instructions"] = "Send a message into one conversation, read the connection status, and read what people wrote with channel_poll (pass the last seq you saw as after, and the epoch it returned). Conversations are opaque strings the service gave.",
    };

    private static JsonArray Tools() =>
    [
        Tool("channel_send", "Send a text message into one conversation, optionally followed by a link.",
            new JsonObject
            {
                ["conversation"] = new JsonObject { ["type"] = "string", ["description"] = "The conversation to write to, as an event named it." },
                ["text"] = new JsonObject { ["type"] = "string", ["description"] = "The message text." },
                ["link"] = new JsonObject { ["type"] = "string", ["format"] = "uri", ["description"] = "Optional absolute http(s) address added after the text." },
            },
            ["conversation", "text"],
            readOnly: false),
        Tool("channel_status", "Read the connection's state: connecting, connected, reconnecting or stopped.", new JsonObject(), [], readOnly: true, openWorld: false),
        Tool("channel_poll", "Read the events after a sequence number (messages people wrote, connection changes), waiting up to timeoutMs for the first one. Reading does not consume events.",
            new JsonObject
            {
                ["after"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0, ["description"] = "The seq of the last event already seen." },
                ["epoch"] = new JsonObject { ["type"] = "string", ["description"] = "The epoch the last read returned. A different one means the connector restarted and the read starts over (reset)." },
                ["timeoutMs"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 30000, ["default"] = 0 },
                ["max"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100, ["default"] = 20 },
            },
            [],
            readOnly: true),
    ];

    private static JsonObject Tool(string name, string description, JsonObject properties, string[] required, bool readOnly, bool openWorld = true)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = readOnly,
                ["destructiveHint"] = false,
                ["idempotentHint"] = readOnly,
                ["openWorldHint"] = openWorld,
            },
        };
    }

    private async Task<JsonObject> CallAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var name = parameters is null ? throw new ArgumentException("name is required.") : String(parameters, "name");
        var arguments = parameters["arguments"] switch
        {
            null => new JsonObject(),
            JsonObject given => given,
            _ => throw new ArgumentException("arguments must be an object."),
        };
        switch (name)
        {
            case "channel_send":
                {
                    var conversation = String(arguments, "conversation");
                    var text = String(arguments, "text");
                    Uri? link = null;
                    if (String(arguments, "link", required: false) is { } raw && !Uri.TryCreate(raw, UriKind.Absolute, out link))
                        return Result(ChannelJson.Send(ChannelSendResult.Fail(ChannelErrorCode.InvalidRequest, "link must be an absolute http or https address.", false)), isError: true);
                    var result = await connector.SendAsync(new ChannelSendRequest(conversation, text, link), cancellationToken).ConfigureAwait(false);
                    return Result(ChannelJson.Send(result), isError: !result.Delivered);
                }

            case "channel_status":
                return Result(ChannelJson.Status(connector.Status), isError: false);

            case "channel_poll":
                {
                    var after = Integer(arguments, "after", 0, 0, long.MaxValue);
                    var timeout = Integer(arguments, "timeoutMs", 0, 0, 30000);
                    var max = Integer(arguments, "max", 20, 1, 100);
                    var poll = await connector.PollAsync(after, String(arguments, "epoch", required: false), (int)max, TimeSpan.FromMilliseconds(timeout), cancellationToken).ConfigureAwait(false);
                    return Result(ChannelJson.Poll(poll), isError: false);
                }

            default:
                throw new ArgumentException($"Unknown tool: {name}");
        }
    }

    private static string String(JsonObject arguments, string name) => String(arguments, name, required: true)!;

    private static string? String(JsonObject arguments, string name, bool required)
    {
        var node = arguments[name];
        if (node is null) return required ? throw new ArgumentException($"{name} is required.") : null;
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new ArgumentException($"{name} must be a string.");
    }

    private static long Integer(JsonObject arguments, string name, long fallback, long minimum, long maximum)
    {
        var node = arguments[name];
        if (node is null) return fallback;
        if (node is not JsonValue value || !value.TryGetValue<long>(out var number)) throw new ArgumentException($"{name} must be an integer.");
        return number < minimum || number > maximum ? throw new ArgumentException($"{name} must be between {minimum} and {maximum}.") : number;
    }

    private static JsonObject Result(JsonObject structured, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = structured.ToJsonString() }),
        ["structuredContent"] = structured,
        ["isError"] = isError,
    };

    private Task ReplyErrorAsync(JsonNode? id, int code, string message) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    private async Task WriteAsync(JsonObject message)
    {
        await writing.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            writing.Release();
        }
    }
}
