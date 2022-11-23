namespace Kassini.Configuration;

public class ProxySection
{
    public string? Destination { get; set; } = null;
    public bool RemovePrefix { get; set; } = true;
}
