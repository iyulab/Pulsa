using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PulsaChannel.Telegram;

/// <summary>
/// Telegram through the Bot API: long polling for messages and <c>sendMessage</c> for replies, over the <see cref="HttpClient"/> the
/// caller gives. Only private (one-to-one) chats are read; messages without text are skipped. The bot token is part of every request
/// address, so nothing this provider returns or throws is built from a request address.
/// </summary>
public sealed class TelegramChannelProvider : IChannelProvider, IChannelProbe, IChannelAcknowledge
{
    /// <summary>The Bot API's longest message.</summary>
    public const int TelegramMaxTextLength = 4096;

    private readonly HttpClient http;
    private readonly string token;
    private readonly TimeProvider time;
    private readonly TimeSpan longPoll;
    private readonly TimeSpan backlogDelay;
    private readonly object ackGate = new();
    // Updates read from the service and not yet confirmed to it, oldest first: the update's id and the event it became (null when it became none).
    private readonly List<(long UpdateId, long? Seq)> unconfirmed = [];
    private long consumedSeq;
    private long confirmedOffset;
    private readonly object gate = new();
    private CancellationTokenSource? stop;
    private Task? loop;
    private IChannelEventSink? sink;
    private ChannelState state = ChannelState.Stopped;
    private DateTimeOffset since;

    /// <param name="token">The bot token. Held only to build request addresses.</param>
    /// <param name="http">The client to use. Its <see cref="HttpClient.BaseAddress"/> is the Bot API root (<c>https://api.telegram.org/</c> when unset) and its timeout must exceed <paramref name="longPollTimeout"/>.</param>
    /// <param name="backlogPause">How long to wait before asking again when the service only repeated updates already read (they stay unconfirmed until the consumer has read past them).</param>
    /// <param name="longPollTimeout">How long one <c>getUpdates</c> call waits at the service for a message (whole seconds).</param>
    public TelegramChannelProvider(string token, HttpClient http, TimeProvider? time = null, TimeSpan? longPollTimeout = null, TimeSpan? backlogPause = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        this.token = token;
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.time = time ?? TimeProvider.System;
        longPoll = longPollTimeout ?? TimeSpan.FromSeconds(25);
        backlogDelay = backlogPause ?? TimeSpan.FromSeconds(1);
        since = this.time.GetUtcNow();
    }

    public string Name => "telegram";

    public int MaxTextLength => TelegramMaxTextLength;

    public ChannelStatus Status
    {
        get { lock (gate) { return new ChannelStatus(Name, state, since); } }
    }

    public Task StartAsync(IChannelEventSink sink, CancellationToken cancellationToken)
    {
        this.sink = sink;
        stop = new CancellationTokenSource();
        Change(ChannelState.Connecting);
        loop = Task.Run(() => PollLoopAsync(stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task<ChannelSendResult> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken)
    {
        var text = request.Link is null ? request.Text : $"{request.Text}\n{request.Link.AbsoluteUri}";
        if (text.Length > TelegramMaxTextLength) return ChannelSendResult.Fail(ChannelErrorCode.InvalidRequest, "text and link together are longer than one Telegram message takes.", retryable: false);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(Method("sendMessage"), new { chat_id = request.Conversation, text }, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return ChannelSendResult.Fail(ChannelErrorCode.NotConnected, "Telegram could not be reached.", retryable: true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ChannelSendResult.Fail(ChannelErrorCode.NotConnected, "Telegram did not answer in time.", retryable: true);
        }

        using (response)
        {
            var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            if (Flag(body?["ok"]) == true && Long(body?["result"]?["message_id"]) is { } id)
            {
                return ChannelSendResult.Ok($"{request.Conversation}:{id}");
            }

            return Map(response.StatusCode, body);
        }
    }

    /// <summary>Asks <c>getMe</c>, which reads no messages, so it confirms nothing to the service.</summary>
    public async Task<ChannelStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        ChannelStatus Status(ChannelState state, string? detail = null) => new(Name, state, time.GetUtcNow(), detail);
        try
        {
            using var response = await http.PostAsync(Method("getMe"), null, cancellationToken).ConfigureAwait(false);
            var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            if (Flag(body?["ok"]) == true) return Status(ChannelState.Connected);
            return response.StatusCode == HttpStatusCode.Unauthorized
                ? Status(ChannelState.Stopped, "the service refused the credential")
                : Status(ChannelState.Reconnecting, "the service did not answer as expected");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Status(ChannelState.Reconnecting, "the service could not be reached");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (stop is null) return;
        await stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try { await loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        Change(ChannelState.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        stop?.Dispose();
    }

    // A leading slash keeps the token's colon from being read as the end of a URI scheme (a relative "bot123:abc/x" is an absolute address).
    private Uri Method(string name) => new(http.BaseAddress ?? DefaultRoot, $"/bot{token}/{name}");

    private static readonly Uri DefaultRoot = new("https://api.telegram.org/");

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                long offset;
                lock (ackGate) { offset = confirmedOffset; }
                using var response = await http.PostAsJsonAsync(
                    Method("getUpdates"),
                    new { offset, timeout = (int)longPoll.TotalSeconds, allowed_updates = new[] { "message" } },
                    cancellationToken).ConfigureAwait(false);
                var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
                if (Flag(body?["ok"]) != true || body?["result"] is not JsonArray updates)
                {
                    // 401 means the token is refused: there is nothing to retry into.
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        ChangeWithDetail(ChannelState.Stopped, "the service refused the credential");
                        return;
                    }

                    throw new HttpRequestException("getUpdates failed");
                }

                backoff = TimeSpan.FromSeconds(1);
                if (state != ChannelState.Connected) Change(ChannelState.Connected);
                var fresh = 0;
                foreach (var update in updates)
                {
                    if (Long(update?["update_id"]) is not { } updateId)
                    {
                        // Cannot be confirmed or told apart from a repeat: read it if it is readable, nothing more.
                        PublishIfMessage(update);
                        continue;
                    }

                    // Updates not yet confirmed to the service come back on every call; the ones already read are not published again. They are
                    // told apart by id, not by "lower than the newest seen": after a week without updates the service picks the next id at random.
                    bool repeat;
                    lock (ackGate) { repeat = unconfirmed.Exists(u => u.UpdateId == updateId); }
                    if (repeat) continue;
                    var seq = PublishIfMessage(update);
                    lock (ackGate) { unconfirmed.Add((updateId, seq)); }
                    fresh++;
                }

                Confirm();
                if (updates.Count > 0 && fresh == 0)
                {
                    // Only repeats: the service answers at once while anything is unconfirmed, so ask again after a pause rather than in a tight loop.
                    await Task.Delay(backlogDelay, time, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (state != ChannelState.Reconnecting) Change(ChannelState.Reconnecting);
                try { await Task.Delay(backoff, time, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    private long? PublishIfMessage(JsonNode? update)
    {
        var message = update?["message"];
        if (message is null || Text(message["chat"]?["type"]) != "private") return null;
        if (Text(message["text"]) is not { Length: > 0 } text) return null;
        if (Long(message["chat"]?["id"]) is not { } chat || Long(message["message_id"]) is not { } messageId) return null;
        var sender = Long(message["from"]?["id"]) ?? chat;
        return sink?.Publish(new MessageReceived(chat.ToString(), sender.ToString(), text, $"{chat}:{messageId}", time.GetUtcNow()));
    }

    /// <summary>The consumer has read past every event up to <paramref name="throughSeq"/>; the service may now be told those updates are taken.</summary>
    public void Acknowledge(long throughSeq)
    {
        lock (ackGate) { consumedSeq = Math.Max(consumedSeq, throughSeq); }
        Confirm();
    }

    // The offset sent to the service moves past an update only when every update up to it is consumed (or became no event), because the
    // service forgets everything below the offset it is given. A restart before that point reads the unconfirmed updates again.
    private void Confirm()
    {
        lock (ackGate)
        {
            while (unconfirmed.Count > 0 && (unconfirmed[0].Seq is null || unconfirmed[0].Seq <= consumedSeq))
            {
                confirmedOffset = unconfirmed[0].UpdateId + 1;
                unconfirmed.RemoveAt(0);
            }
        }
    }

    // The service's JSON is read without assuming its shape: a field of another type is "not there", never an exception.
    private static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static bool? Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChannelSendResult Map(HttpStatusCode status, JsonNode? body)
    {
        var retryAfter = Long(body?["parameters"]?["retry_after"]);
        return status switch
        {
            HttpStatusCode.TooManyRequests => ChannelSendResult.Fail(ChannelErrorCode.RateLimited, retryAfter is { } s ? $"Telegram asked to wait {s} seconds." : "Telegram asked to slow down.", retryable: true),
            HttpStatusCode.BadRequest or HttpStatusCode.Forbidden => ChannelSendResult.Fail(ChannelErrorCode.UnknownConversation, "Telegram does not let the bot write to that chat.", retryable: false),
            HttpStatusCode.Unauthorized => ChannelSendResult.Fail(ChannelErrorCode.ServiceError, "Telegram refused the credential.", retryable: false),
            >= HttpStatusCode.InternalServerError => ChannelSendResult.Fail(ChannelErrorCode.ServiceError, "Telegram failed to take the message.", retryable: true),
            _ => ChannelSendResult.Fail(ChannelErrorCode.ServiceError, "Telegram did not take the message.", retryable: false),
        };
    }

    private void Change(ChannelState next) => ChangeWithDetail(next, null);

    private void ChangeWithDetail(ChannelState next, string? detail)
    {
        lock (gate)
        {
            state = next;
            since = time.GetUtcNow();
        }

        sink?.Publish(new ConnectionChanged(next, detail));
    }
}
