using IndexThinking.Client;
using IndexThinking.Extensions;
using Microsoft.Extensions.AI;
using OpenAI;

namespace PulsaLLM;

/// <summary>
/// Creates and caches IChatClient instances per unique provider configuration.
/// </summary>
public class ChatClientFactory(IServiceProvider serviceProvider) : IDisposable
{
    private readonly Dictionary<string, IChatClient> _cache = [];

    public IChatClient GetOrCreate(ProviderOptions opts)
    {
        var cacheKey = $"{opts.Type}|{opts.Host}|{opts.Model}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        IChatClient innerClient = opts.Type.ToLowerInvariant() switch
        {
            "openai" => CreateOpenAiClient(opts),
            "openai-compatible" => CreateOpenAiCompatibleClient(opts),
            _ => throw new InvalidOperationException(
                $"Unsupported provider type: '{opts.Type}'. Use 'openai' or 'openai-compatible'."),
        };

        var client = new ChatClientBuilder(innerClient)
            .UseIndexThinking(thinkingOpts =>
            {
                thinkingOpts.EnableReasoning = false;
                thinkingOpts.EnableContextTracking = false;
                thinkingOpts.EnableContextInjection = false;
                thinkingOpts.DefaultContinuation = new()
                {
                    MaxContinuations = 3,
                    MaxContextTokens = opts.ContextWindow > 0 ? opts.ContextWindow : null,
                };
                thinkingOpts.ReasoningRequestSettings = new()
                {
                    UseAlternativeQwenField = true,
                };
            })
            .Build(serviceProvider);

        _cache[cacheKey] = client;
        return client;
    }

    private static IChatClient CreateOpenAiClient(ProviderOptions opts)
    {
        var clientOptions = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromMinutes(10),
        };
        var credential = new System.ClientModel.ApiKeyCredential(opts.ApiKey);
        var openAiClient = new OpenAIClient(credential, clientOptions);
        return openAiClient.GetChatClient(opts.Model).AsIChatClient();
    }

    private static IChatClient CreateOpenAiCompatibleClient(ProviderOptions opts)
    {
        var endpoint = new Uri(opts.Host.TrimEnd('/') + opts.PathPrefix);
        var credential = new System.ClientModel.ApiKeyCredential(opts.ApiKey);
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = TimeSpan.FromMinutes(10),
        };
        var openAiClient = new OpenAIClient(credential, clientOptions);
        return openAiClient.GetChatClient(opts.Model).AsIChatClient();
    }

    public void Dispose()
    {
        foreach (var client in _cache.Values)
        {
            if (client is IDisposable d) d.Dispose();
        }
        _cache.Clear();
    }
}
