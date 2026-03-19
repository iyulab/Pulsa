namespace PulsaPDFDiff;

public class PromptManager
{
    private readonly string _promptsDir;

    public PromptManager(string promptsDir)
    {
        _promptsDir = promptsDir;
        Directory.CreateDirectory(_promptsDir);
    }

    public List<string> List()
    {
        return Directory.GetFiles(_promptsDir, "*.txt")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(n => n)
            .ToList()!;
    }

    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        var path = ResolvePath(name);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }

    public async Task SaveAsync(string name, string content, CancellationToken ct = default)
    {
        var path = ResolvePath(name);
        await File.WriteAllTextAsync(path, content, ct);
    }

    private string ResolvePath(string name) =>
        Path.Combine(_promptsDir, $"{name}.txt");
}
