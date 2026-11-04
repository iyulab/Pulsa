namespace PulsaChannel;

/// <summary>Takes the configured credentials out of any text before it is written anywhere.</summary>
public sealed class SecretRedactor
{
    private readonly string[] secrets;

    public SecretRedactor(IEnumerable<string?> secrets)
    {
        this.secrets = secrets.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().OrderByDescending(s => s.Length).ToArray();
    }

    public string Redact(string text)
    {
        foreach (var secret in secrets) text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text;
    }
}
