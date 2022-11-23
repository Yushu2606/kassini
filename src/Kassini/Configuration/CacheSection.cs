namespace Kassini.Configuration;

public class CacheSection : CacheSettings
{
    public bool Enabled { get; set; } = true;

    public string? Policy { get; set; } = null;
}
