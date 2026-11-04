using PulsaChannel.Telegram;

namespace PulsaChannel.Cli;

/// <summary>
/// Chooses the provider from the process's environment. Credentials come from the environment only — never from the command line,
/// where they would show in a process listing.
/// </summary>
public static class ProviderFactory
{
    public const string ProviderVariable = "PULSA_CHANNEL_PROVIDER";
    public const string TelegramTokenVariable = "PULSA_CHANNEL_TELEGRAM_TOKEN";
    public const string TelegramApiVariable = "PULSA_CHANNEL_TELEGRAM_API";
    public const string FakeInboundVariable = "PULSA_CHANNEL_FAKE_INBOUND";

    /// <returns>
    /// The provider, the credentials that must never be written anywhere, and whether a one-shot send has to start the provider first.
    /// A real service's provider does not: starting it would begin reading messages, and reading is what confirms them to the service,
    /// so a send that reads would take messages nobody has seen.
    /// </returns>
    public static (IChannelProvider Provider, IReadOnlyList<string> Secrets, bool StartToSend) Create(string? name, Func<string, string?> environment)
    {
        name ??= environment(ProviderVariable) ?? "fake";
        switch (name)
        {
            case "fake":
                {
                    var fake = new FakeChannelProvider();
                    // "conversation|sender|text" — one inbound message the fake receives as soon as it starts, so a consumer can see an event without an account.
                    var inbound = environment(FakeInboundVariable);
                    return (inbound is null ? fake : new ScriptedFake(fake, inbound), [], true);
                }

            case "telegram":
                {
                    var token = environment(TelegramTokenVariable);
                    if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException($"{TelegramTokenVariable} is not set.");
                    var api = environment(TelegramApiVariable);
                    var http = new HttpClient { BaseAddress = new Uri(api ?? "https://api.telegram.org/"), Timeout = TimeSpan.FromSeconds(60) };
                    return (new TelegramChannelProvider(token, http), [token], false);
                }

            default:
                throw new ArgumentException($"Unknown provider '{name}'. Use fake or telegram.");
        }
    }

    private sealed class ScriptedFake(FakeChannelProvider inner, string inbound) : IChannelProvider
    {
        public string Name => inner.Name;

        public int MaxTextLength => inner.MaxTextLength;

        public ChannelStatus Status => inner.Status;

        public async Task StartAsync(IChannelEventSink sink, CancellationToken cancellationToken)
        {
            await inner.StartAsync(sink, cancellationToken).ConfigureAwait(false);
            var parts = inbound.Split('|', 3);
            if (parts.Length == 3) inner.Receive(parts[0], parts[1], parts[2]);
        }

        public Task<ChannelSendResult> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken) => inner.SendAsync(request, cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => inner.StopAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
