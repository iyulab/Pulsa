using System.Text.Json.Nodes;

namespace PulsaChannel.Cli;

/// <summary>The wire shapes of the contract (docs/channel-connector.md): snake_case names, ISO-8601 times.</summary>
public static class ChannelJson
{
    public static string Code(ChannelErrorCode code) => code switch
    {
        ChannelErrorCode.InvalidRequest => "invalid_request",
        ChannelErrorCode.UnknownConversation => "unknown_conversation",
        ChannelErrorCode.NotConnected => "not_connected",
        ChannelErrorCode.RateLimited => "rate_limited",
        _ => "service_error",
    };

    public static string State(ChannelState state) => state switch
    {
        ChannelState.Connecting => "connecting",
        ChannelState.Connected => "connected",
        ChannelState.Reconnecting => "reconnecting",
        _ => "stopped",
    };

    public static JsonObject Error(ChannelError error) => new() { ["code"] = Code(error.Code), ["message"] = error.Message, ["retryable"] = error.Retryable };

    public static JsonObject Send(ChannelSendResult result)
    {
        var json = new JsonObject { ["delivered"] = result.Delivered };
        if (result.MessageId is not null) json["messageId"] = result.MessageId;
        if (result.Error is not null) json["error"] = Error(result.Error);
        return json;
    }

    public static JsonObject Status(ChannelStatus status)
    {
        var json = new JsonObject { ["provider"] = status.Provider, ["state"] = State(status.State), ["since"] = status.Since.ToString("O") };
        if (status.Detail is not null) json["detail"] = status.Detail;
        return json;
    }

    public static JsonObject Event(ChannelEvent channelEvent)
    {
        var json = new JsonObject { ["seq"] = channelEvent.Seq, ["type"] = channelEvent.Type, ["at"] = channelEvent.At.ToString("O") };
        switch (channelEvent)
        {
            case MessageReceived m:
                json["conversation"] = m.Conversation;
                json["sender"] = m.Sender;
                json["text"] = m.Text;
                json["messageId"] = m.MessageId;
                json["receivedAt"] = m.ReceivedAt.ToString("O");
                break;
            case ConnectionChanged c:
                json["state"] = State(c.State);
                if (c.Detail is not null) json["detail"] = c.Detail;
                break;
        }

        return json;
    }

    public static JsonObject Poll(ChannelPoll poll)
    {
        var array = new JsonArray();
        foreach (var e in poll.Events) array.Add(Event(e));
        return new JsonObject { ["epoch"] = poll.Epoch, ["reset"] = poll.Reset, ["gap"] = poll.Gap, ["events"] = array };
    }
}
