using System.Diagnostics.CodeAnalysis;

namespace Kassini.Configuration;

public class EndpointSection
{
    public string? Route { get; set; } = null;

    public string? Group { get; set; } = null;

    // Response headers
    public Dictionary<string, string>? Headers { get; set; } = null;

    // Content-Type header
    public string? ContentType { get; set; } = null;

    // Static files provider
    public FilesSection? Files { get; set; } = null;

    // Status code
    public int? Status { get; set; } = null;

    // Response body
    public string? Body { get; set; } = null;

    // Response body file path
    public FileSection? File { get; set; } = null;

    // Filters
    public FilterSection[] Filters { get; set; } = [];

    // Proxy
    public ProxySection? Proxy { get; set; } = null;

    // Cache
    public CacheSection? Cache { get; set; } = null;

    // Rate limitting
    public RateLimitSection? RateLimit { get; set; } = null;

    // Redirection
    public string? Redirect { get; set; } = null;

    // Method 
    public string Methods { get; set; } = "GET";

    public string[] GetMethods() => Methods.Split(' ', ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
