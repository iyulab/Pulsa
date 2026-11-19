namespace PulsaChannel;

/// <summary>The closed set of reasons a channel call can fail, so a caller can branch without reading text.</summary>
public enum ChannelErrorCode
{
    InvalidRequest,
    UnknownConversation,
    NotConnected,
    RateLimited,
    ServiceError,
    NotSupported,
}

/// <param name="Code">Why the call failed.</param>
/// <param name="Message">For a person to read. Holds no credential and no message text.</param>
/// <param name="Retryable">Whether the same call may succeed later.</param>
public sealed record ChannelError(ChannelErrorCode Code, string Message, bool Retryable);

public enum ChannelState
{
    Connecting,
    Connected,
    Reconnecting,
    Stopped,
}

/// <param name="Provider">The provider's name.</param>
/// <param name="State">The connection's state.</param>
/// <param name="Since">When the connection entered this state.</param>
/// <param name="Detail">A short note, never a credential.</param>
public sealed record ChannelStatus(string Provider, ChannelState State, DateTimeOffset Since, string? Detail = null);

/// <param name="Conversation">The conversation to write to, as the provider named it (opaque to everything else).</param>
/// <param name="Text">The text to send.</param>
/// <param name="Link">An optional absolute http(s) address appended after the text.</param>
/// <param name="Choices">Optional small set of answers offered with the message, for the person to pick one from; null or empty offers none.</param>
public sealed record ChannelSendRequest(string Conversation, string Text, Uri? Link = null, IReadOnlyList<ChannelChoice>? Choices = null);

/// <summary>One answer offered with a message. The connector never reads what a choice means.</summary>
/// <param name="Id">An opaque identifier the consumer gets back when the person picks this choice; unique within one message.</param>
/// <param name="Label">What the person sees on the choice.</param>
public sealed record ChannelChoice(string Id, string Label);

/// <summary>What a provider's service takes as choices on one message.</summary>
/// <param name="MaxChoices">The most choices one message carries.</param>
/// <param name="MaxIdBytes">The longest choice id, in UTF-8 bytes.</param>
/// <param name="MaxLabelLength">The longest choice label, in characters.</param>
public sealed record ChannelChoiceSupport(int MaxChoices, int MaxIdBytes, int MaxLabelLength);

public sealed record ChannelSendResult(bool Delivered, string? MessageId = null, ChannelError? Error = null)
{
    public static ChannelSendResult Ok(string messageId) => new(true, messageId);

    public static ChannelSendResult Fail(ChannelErrorCode code, string message, bool retryable) => new(false, null, new ChannelError(code, message, retryable));
}

/// <summary>What one read of the event stream returned, and whether the cursor it was read from still meant anything.</summary>
/// <param name="Epoch">Names the running connector the events come from; hand it back on the next read.</param>
/// <param name="Events">The events after the cursor, oldest first.</param>
/// <param name="Reset">The cursor came from another run (its epoch differs, or it is ahead of anything this run has produced), so the read started from the beginning.</param>
/// <param name="Gap">Events between the cursor and the first one returned were dropped because more than the queue's capacity arrived unread.</param>
public sealed record ChannelPoll(string Epoch, IReadOnlyList<ChannelEvent> Events, bool Reset, bool Gap);

/// <summary>Something that happened on the connection. <see cref="Seq"/> and <see cref="At"/> are set by the event queue.</summary>
public abstract record ChannelEvent
{
    /// <summary>Increases by one per event of a running connector; starts again from 1 after a restart.</summary>
    public long Seq { get; init; }

    public DateTimeOffset At { get; init; }

    /// <summary>The event's wire name.</summary>
    public abstract string Type { get; }
}

/// <param name="Conversation">The conversation the message came in on (opaque).</param>
/// <param name="Sender">Who sent it, as an opaque label.</param>
/// <param name="Text">The message text.</param>
/// <param name="MessageId">The service's own id for the message; a consumer de-duplicates by it.</param>
/// <param name="ReceivedAt">When the connector received it.</param>
public sealed record MessageReceived(string Conversation, string Sender, string Text, string MessageId, DateTimeOffset ReceivedAt) : ChannelEvent
{
    public override string Type => "message_received";
}

public sealed record ConnectionChanged(ChannelState State, string? Detail = null) : ChannelEvent
{
    public override string Type => "connection_changed";
}

/// <summary>
/// A person picked one of the choices offered with a message. Exactly one of these exists per message: a second pick of the same
/// message is told "already chosen" by the provider and never reaches the consumer.
/// </summary>
/// <param name="Conversation">The conversation the message is in (opaque).</param>
/// <param name="Sender">Who picked, as an opaque label.</param>
/// <param name="MessageId">The id the send returned for the message the choice was offered with.</param>
/// <param name="ChoiceId">The <see cref="ChannelChoice.Id"/> that was picked.</param>
/// <param name="Closed">The provider took the choices off the message, so they cannot be picked again by anyone who still sees it.</param>
public sealed record ChoiceSelected(string Conversation, string Sender, string MessageId, string ChoiceId, bool Closed) : ChannelEvent
{
    public override string Type => "choice_selected";
}
