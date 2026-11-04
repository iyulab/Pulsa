namespace PulsaChannel;

/// <summary>Where a provider reports what happens on its connection.</summary>
public interface IChannelEventSink
{
    void Publish(ChannelEvent channelEvent);
}

/// <summary>
/// A provider that can answer "is the connection usable?" without starting to read messages. Reading is what confirms messages to
/// some services, so a one-shot status check must not use <see cref="IChannelProvider.StartAsync"/>.
/// </summary>
public interface IChannelProbe
{
    Task<ChannelStatus> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>One messaging service behind the shared contract.</summary>
public interface IChannelProvider : IAsyncDisposable
{
    /// <summary>A short lowercase name, for example <c>telegram</c>.</summary>
    string Name { get; }

    /// <summary>The longest text the service takes in one message.</summary>
    int MaxTextLength { get; }

    ChannelStatus Status { get; }

    /// <summary>Opens the connection and starts publishing events. Returns once the provider is running, not once it is connected.</summary>
    Task StartAsync(IChannelEventSink sink, CancellationToken cancellationToken);

    /// <summary>Sends one message. A failure the service reports comes back as a result; this method throws only for a bug.</summary>
    Task<ChannelSendResult> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
