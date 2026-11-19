namespace PulsaChannel;

/// <summary>
/// The connector's behaviour over one provider — what the tools and the CLI do. It checks the request before the provider sees it,
/// turns a provider that throws into the contract's error vocabulary, and keeps credentials out of what it returns.
/// </summary>
public sealed class ChannelConnector : IAsyncDisposable
{
    private readonly IChannelProvider provider;
    private readonly SecretRedactor redactor;
    private readonly string? fileRoot;

    /// <param name="fileRoot">The folder files may be sent from; null when this connector sends no files. A request names a path under it, never outside it.</param>
    public ChannelConnector(IChannelProvider provider, IEnumerable<string?>? secrets = null, ChannelEventQueue? events = null, string? fileRoot = null)
    {
        this.fileRoot = string.IsNullOrWhiteSpace(fileRoot) ? null : Path.GetFullPath(fileRoot);
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
        var refused = Validate(request);
        if (refused is not null) return refused;
        if (request.File is not null)
        {
            var attachment = ResolveFile(request.File, out refused);
            if (refused is not null) return refused;
            request = request with { Attachment = attachment };
        }

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

    private ChannelSendResult? Validate(ChannelSendRequest request)
    {
        static ChannelSendResult Invalid(string message) => ChannelSendResult.Fail(ChannelErrorCode.InvalidRequest, message, retryable: false);
        if (string.IsNullOrWhiteSpace(request.Conversation)) return Invalid("conversation must not be empty.");
        if (string.IsNullOrWhiteSpace(request.Text)) return Invalid("text must not be empty.");
        if (request.File is null && request.Text.Length > provider.MaxTextLength) return Invalid($"text is longer than the {provider.MaxTextLength} characters this service takes in one message.");
        if (request.Link is not null && (!request.Link.IsAbsoluteUri || request.Link.Scheme is not ("http" or "https"))) return Invalid("link must be an absolute http or https address.");
        if (request.File is not null && ValidateCaption(request) is { } caption) return caption;
        return request.Choices is { Count: > 0 } choices ? ValidateChoices(choices) : null;
    }

    // With a file the text is its caption, and the caption's limit is the service's (usually shorter than a message's).
    private ChannelSendResult? ValidateCaption(ChannelSendRequest request)
    {
        if (provider.Files is not { } support)
            return ChannelSendResult.Fail(ChannelErrorCode.NotSupported, $"The {provider.Name} provider does not send files.", retryable: false);
        var caption = request.Link is null ? request.Text : $"{request.Text}\n{request.Link.AbsoluteUri}";
        return caption.Length > support.MaxCaptionLength
            ? ChannelSendResult.Fail(ChannelErrorCode.InvalidRequest, $"text and link together are longer than the {support.MaxCaptionLength} characters this service takes as a file's caption.", retryable: false)
            : null;
    }

    // The file must be a regular file under the folder this connector was given. A path that is absolute, climbs out, or goes through a link
    // is refused: the caller is a model's tool call, and what it names must stay inside what the host chose to share.
    private ChannelAttachment? ResolveFile(string relative, out ChannelSendResult? refused)
    {
        static ChannelSendResult Invalid(string message) => ChannelSendResult.Fail(ChannelErrorCode.InvalidRequest, message, retryable: false);
        refused = null;
        if (fileRoot is null)
        {
            refused = ChannelSendResult.Fail(ChannelErrorCode.NotSupported, "This connector was not given a folder to send files from.", retryable: false);
            return null;
        }

        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            refused = Invalid("file must be a path under the folder files are sent from.");
            return null;
        }

        var segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            refused = Invalid("file must be a path under the folder files are sent from.");
            return null;
        }

        var current = fileRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!entry.Exists)
            {
                refused = Invalid("that file does not exist under the folder files are sent from.");
                return null;
            }

            if (entry.LinkTarget is not null)
            {
                refused = Invalid("file must not go through a link.");
                return null;
            }
        }

        if (new FileInfo(current) is not { Exists: true } info)
        {
            refused = Invalid("file must name a file, not a folder.");
            return null;
        }

        var support = provider.Files!;
        if (info.Length == 0)
        {
            refused = Invalid("the file is empty.");
            return null;
        }

        if (info.Length > support.MaxBytes)
        {
            refused = Invalid($"the file is larger than the {support.MaxBytes} bytes this service takes.");
            return null;
        }

        var path = info.FullName;
        return new ChannelAttachment(info.Name, MediaTypeOf(info.Extension), info.Length, () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    private static string MediaTypeOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" or ".log" => "text/plain",
        ".md" => "text/markdown",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".zip" => "application/zip",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };

    private ChannelSendResult? ValidateChoices(IReadOnlyList<ChannelChoice> choices)
    {
        static ChannelSendResult Invalid(string message) => ChannelSendResult.Fail(ChannelErrorCode.InvalidRequest, message, retryable: false);
        // A service that has no choices does not get the message as plain text: the caller may be relying on the person's pick, and decides what to do instead.
        if (provider.Choices is not { } support)
            return ChannelSendResult.Fail(ChannelErrorCode.NotSupported, $"The {provider.Name} provider does not offer choices on a message.", retryable: false);
        if (choices.Count > support.MaxChoices) return Invalid($"a message takes at most {support.MaxChoices} choices.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var choice in choices)
        {
            if (choice is null || string.IsNullOrWhiteSpace(choice.Id) || string.IsNullOrWhiteSpace(choice.Label)) return Invalid("every choice needs an id and a label.");
            if (System.Text.Encoding.UTF8.GetByteCount(choice.Id) > support.MaxIdBytes) return Invalid($"a choice id is at most {support.MaxIdBytes} bytes.");
            if (choice.Label.Length > support.MaxLabelLength) return Invalid($"a choice label is at most {support.MaxLabelLength} characters.");
            if (!ids.Add(choice.Id)) return Invalid("choice ids must differ within one message.");
        }

        return null;
    }
}
