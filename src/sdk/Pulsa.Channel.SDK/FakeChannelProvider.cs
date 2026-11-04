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

    public FakeChannelProvider(IEnumerable<string>? conversations = null, int maxTextLength = 4096, TimeProvider? time = null)
    {
        this.conversations = new HashSet<string>(conversations ?? ["conversation-1"], StringComparer.Ordinal);
        MaxTextLength = maxTextLength;
        this.time = time ?? TimeProvider.System;
        since = this.time.GetUtcNow();
    }

    public string Name => "fake";

    public int MaxTextLength { get; }

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
            return Task.FromResult(ChannelSendResult.Ok($"fake-{++messages}"));
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
