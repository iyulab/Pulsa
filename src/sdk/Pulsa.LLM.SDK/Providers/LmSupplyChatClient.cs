using LMSupply.Generator;
using LMSupply.Generator.Abstractions;
using LMSupply.Generator.Models;
using Microsoft.Extensions.AI;
using LmChatMessage = LMSupply.Generator.Models.ChatMessage;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace PulsaLLM.Providers;

public sealed class LmSupplyChatClient : IChatClient
{
    private readonly ITextGenerator _generator;

    private LmSupplyChatClient(ITextGenerator generator)
    {
        _generator = generator;
    }

    public static async Task<LmSupplyChatClient> CreateAsync(
        ProviderOptions options, CancellationToken ct)
    {
        var builder = TextGeneratorBuilder.Create();
        var preset = options.Model.ToLowerInvariant() switch
        {
            "fast" or "small" => GeneratorModelPreset.Fast,
            "quality" => GeneratorModelPreset.Quality,
            _ => GeneratorModelPreset.Default,
        };
        builder.WithModel(preset);

        var model = await builder.BuildAsync(ct);
        var generator = model as ITextGenerator
            ?? throw new InvalidOperationException(
                "Built model does not implement ITextGenerator");

        return new LmSupplyChatClient(generator);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<AiChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var lmMessages = messages.Select(m => m.Role.Value switch
        {
            "system" => LmChatMessage.System(m.Text ?? ""),
            "assistant" => LmChatMessage.Assistant(m.Text ?? ""),
            _ => LmChatMessage.User(m.Text ?? ""),
        }).ToArray();

        var result = await _generator.GenerateChatCompleteAsync(
            lmMessages, cancellationToken: cancellationToken);

        return new ChatResponse(
            [new AiChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, result)])
        {
            FinishReason = ChatFinishReason.Stop
        };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AiChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LmSupply does not support streaming.");

    public void Dispose() => (_generator as IDisposable)?.Dispose();

    public ChatClientMetadata Metadata => new(nameof(LmSupplyChatClient));
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}
