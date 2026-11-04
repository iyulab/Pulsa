namespace PulsaChannel;

/// <summary>
/// The connector's behaviour over one provider — what the tools and the CLI do. It checks the request before the provider sees it,
/// turns a provider that throws into the contract's error vocabulary, and keeps credentials out of what it returns.
/// </summary>
public sealed class ChannelConnector : IAsyncDisposable
{
    private readonly IChannelProvider provider;
    private readonly SecretRedactor redactor;

    public ChannelConnector(IChannelProvider provider, IEnumerable<string?>? secrets = null, ChannelEventQueue? events = null)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        redactor = new SecretRedactor(secrets ?? []);
        Events = events ?? new ChannelEventQueue();
    }

    public ChannelEventQueue Events { get; }

    public ChannelStatus Status => provider.Status;

    public Task StartAsync(CancellationToken cancellationToken) => provider.StartAsync(Events, cancellationToken);

    public async Task<ChannelSendResult> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invalid = Validate(request);
        if (invalid is not null) return ChannelSendResult.Fail(ChannelErrorCode.InvalidRequest, invalid, retryable: false);

        try
        {
            var result = await provider.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return result.Error is null ? result : result with { Error = result.Error with { Message = redactor.Redact(result.Error.Message) } };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // What the exception says may hold a credential or the message; the contract's error carries neither.
            return ChannelSendResult.Fail(ChannelErrorCode.ServiceError, $"The {provider.Name} provider failed while sending.", retryable: false);
        }
    }

    /// <param name="after">The <see cref="ChannelEvent.Seq"/> last seen.</param>
    /// <param name="epoch">The <see cref="ChannelPoll.Epoch"/> of the read that gave <paramref name="after"/>, when the caller has one.</param>
    public async Task<ChannelPoll> PollAsync(long after, string? epoch, int max, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (max is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(max), "max must be between 1 and 100.");
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout), "timeout must be between 0 and 30 seconds.");
        if (after < 0) throw new ArgumentOutOfRangeException(nameof(after), "after must not be negative.");

        // A cursor from another run says nothing about this one: sequence numbers start again from 1 after a restart.
        var reset = (epoch is not null && epoch != Events.Epoch) || after > Events.Last;
        if (reset) after = 0;
        else if (after > 0 && provider is IChannelAcknowledge acknowledge) acknowledge.Acknowledge(after);
        var oldest = Events.Oldest;
        var gap = !reset && oldest > 0 && after + 1 < oldest;
        var events = await Events.PollAsync(after, max, timeout, cancellationToken).ConfigureAwait(false);
        return new ChannelPoll(Events.Epoch, events, reset, gap);
    }

    public Task StopAsync(CancellationToken cancellationToken) => provider.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => provider.DisposeAsync();

    private string? Validate(ChannelSendRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Conversation)) return "conversation must not be empty.";
        if (string.IsNullOrWhiteSpace(request.Text)) return "text must not be empty.";
        if (request.Text.Length > provider.MaxTextLength) return $"text is longer than the {provider.MaxTextLength} characters this service takes in one message.";
        if (request.Link is not null && (!request.Link.IsAbsoluteUri || request.Link.Scheme is not ("http" or "https"))) return "link must be an absolute http or https address.";
        return null;
    }
}
