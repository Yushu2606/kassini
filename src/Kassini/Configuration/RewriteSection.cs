namespace Kassini.Configuration;

public class RewriteSection
{
    public string? From { get; set; } = null;
    public string? To { get; set; } = null;
    public bool SkipRemainingRules { get; set; } = true;
}
