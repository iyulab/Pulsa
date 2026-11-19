namespace PulsaChannel;

/// <summary>An in-memory provider: a consumer and the connector's own tests run without an account or a network.</summary>
public sealed class FakeChannelProvider : IChannelProvider
{
    private readonly object gate = new();
    private readonly List<ChannelSendRequest> sent = [];
    private readonly HashSet<string> conversations;
    private readonly TimeProvider time;
    private IChannelEventSink? sink;
    private ChannelState state = ChannelState.Stopped;
    private DateTimeOffset since;
    private ChannelSendResult? nextFailure;
    private int messages;
    // For each message sent with choices: the ids it offered, and whether one was picked (a message takes one pick).
    private readonly Dictionary<string, (string Conversation, HashSet<string> Ids, bool Picked)> offered = new(StringComparer.Ordinal);
    private readonly ChannelChoiceSupport? choices;

    /// <param name="choices">What this provider takes as choices on a message; null to be a service that takes none.</param>
    public FakeChannelProvider(IEnumerable<string>? conversations = null, int maxTextLength = 4096, TimeProvider? time = null, ChannelChoiceSupport? choices = null)
    {
        this.choices = choices ?? new ChannelChoiceSupport(8, 64, 64);
        this.conversations = new HashSet<string>(conversations ?? ["conversation-1"], StringComparer.Ordinal);
        MaxTextLength = maxTextLength;
        this.time = time ?? TimeProvider.System;
        since = this.time.GetUtcNow();
    }

    public string Name => "fake";

    public int MaxTextLength { get; }

    public ChannelChoiceSupport? Choices => choices;

    public ChannelStatus Status
    {
        get { lock (gate) { return new ChannelStatus(Name, state, since); } }
    }

    /// <summary>What was sent through this provider, oldest first.</summary>
    public IReadOnlyList<ChannelSendRequest> Sent
    {
        get { lock (gate) { return sent.ToList(); } }
    }

    public Task StartAsync(IChannelEventSink sink, CancellationToken cancellationToken)
    {
        this.sink = sink;
        Change(ChannelState.Connected);
        return Task.CompletedTask;
    }

    public Task<ChannelSendResult> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (state != ChannelState.Connected) return Task.FromResult(ChannelSendResult.Fail(ChannelErrorCode.NotConnected, "The fake provider is not connected.", retryable: true));
            if (nextFailure is { } failure)
            {
                nextFailure = null;
                return Task.FromResult(failure);
            }

            if (!conversations.Contains(request.Conversation)) return Task.FromResult(ChannelSendResult.Fail(ChannelErrorCode.UnknownConversation, "The fake provider does not know that conversation.", retryable: false));
            sent.Add(request);
            var id = $"fake-{++messages}";
            if (request.Choices is { Count: > 0 } offeredChoices) offered[id] = (request.Conversation, offeredChoices.Select(c => c.Id).ToHashSet(StringComparer.Ordinal), false);
            return Task.FromResult(ChannelSendResult.Ok(id));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Change(ChannelState.Stopped);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>A person writes to the agent.</summary>
    public void Receive(string conversation, string sender, string text)
    {
        string id;
        lock (gate)
        {
            conversations.Add(conversation);
            id = $"fake-in-{++messages}";
        }

        sink?.Publish(new MessageReceived(conversation, sender, text, id, time.GetUtcNow()));
    }

    /// <summary>
    /// A person picks one of the choices offered with a message the agent sent.
    /// </summary>
    /// <returns>True when it was taken (the event is published and the choices are closed); false when the message offered no such choice or was already picked.</returns>
    public bool Choose(string sender, string messageId, string choiceId)
    {
        string conversation;
        lock (gate)
        {
            if (!offered.TryGetValue(messageId, out var message) || message.Picked || !message.Ids.Contains(choiceId)) return false;
            offered[messageId] = message with { Picked = true };
            conversation = message.Conversation;
        }

        sink?.Publish(new ChoiceSelected(conversation, sender, messageId, choiceId, Closed: true));
        return true;
    }

    /// <summary>The connection drops; sends fail with <see cref="ChannelErrorCode.NotConnected"/> until <see cref="Reconnect"/>.</summary>
    public void Disconnect() => Change(ChannelState.Reconnecting);

    public void Reconnect() => Change(ChannelState.Connected);

    /// <summary>The next send fails with this result instead of going through.</summary>
    public void FailNextSend(ChannelSendResult failure)
    {
        lock (gate) { nextFailure = failure; }
    }

    private void Change(ChannelState next)
    {
        lock (gate)
        {
            state = next;
            since = time.GetUtcNow();
        }

        sink?.Publish(new ConnectionChanged(next));
    }
}
