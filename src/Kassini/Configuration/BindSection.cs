using Microsoft.Extensions.Logging.Abstractions;

namespace Kassini.Configuration;

public class BindSection
{
    public string? Address { get; set; } = null;
    public string? Certificate { get; set; } = null;
    public string[] Protocols { get; set; } = Array.Empty<string>();
}
