namespace Kassini.Configuration;

public class RateLimitSection : RateLimitSettings
{
    public bool Enabled { get; set; } = true;

    public string? Policy { get; set; } = null;
}
