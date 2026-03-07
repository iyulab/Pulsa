namespace PulsaLLM.Providers;

public interface ILlmProvider : IAsyncDisposable
{
    Task<string> GenerateAsync(string systemPrompt, string userContent, CancellationToken ct);
}
