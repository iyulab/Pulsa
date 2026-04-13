namespace PulsaVault;

public class PulsaVaultOptions
{
    public List<VaultFolderOptions> Folders { get; set; } = [];
}

public class VaultFolderOptions
{
    public required string Path { get; set; }
    public List<string> IncludePatterns { get; set; } = ["*.pdf", "*.docx", "*.md", "*.txt"];
    public List<string> ExcludePatterns { get; set; } = ["*.tmp", "~$*"];
    public bool Recursive { get; set; } = true;
}
