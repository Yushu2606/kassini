namespace Kassini.Configuration;

public class FilesSection
{
    public string? Path { get; set; } = null;

    public Dictionary<string, string> MimeTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
