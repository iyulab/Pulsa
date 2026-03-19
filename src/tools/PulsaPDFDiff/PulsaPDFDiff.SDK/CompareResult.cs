namespace PulsaPDFDiff;

public class CompareResult
{
    public string Text { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens => PromptTokens + CompletionTokens;
}
