namespace Kassini.Configuration;

public class PolicySection
{
    public string[]? Roles { get; set; } = null;

    public Dictionary<string, string>? Claims { get; set; } = null;
}
