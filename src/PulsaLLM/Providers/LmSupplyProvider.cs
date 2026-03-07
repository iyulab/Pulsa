using LMSupply.Generator;
using LMSupply.Generator.Abstractions;
using LMSupply.Generator.Models;
using Microsoft.Extensions.Logging;

namespace PulsaLLM.Providers;

public class LmSupplyProvider : ILlmProvider
{
    private readonly ITextGenerator _generator;
    private readonly ILogger _logger;

    private LmSupplyProvider(ITextGenerator generator, ILogger logger)
    {
        _generator = generator;
        _logger = logger;
    }

    public static async Task<LmSupplyProvider> CreateAsync(ProviderOptions options, ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("Loading LM-Supply model: {Model}...", options.Model);

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
            ?? throw new InvalidOperationException("Built model does not implement ITextGenerator");

        logger.LogInformation("LM-Supply model loaded: {ModelId}", generator.ModelId);
        return new LmSupplyProvider(generator, logger);
    }

    public async Task<string> GenerateAsync(string systemPrompt, string userContent, CancellationToken ct)
    {
        var messages = new[]
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(userContent),
        };

        return await _generator.GenerateChatCompleteAsync(messages, cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _generator.DisposeAsync();
    }
}
