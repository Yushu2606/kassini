namespace Kassini.Configuration;

public class ConfigurationSection
{
    private Dictionary<string, CachePolicySection>? _cachePolicies;
    private Dictionary<string, RateLimitPolicySection>? _rateLimitpolicies;

    public ServerSection[] Servers { get; set; } = Array.Empty<ServerSection>();

    public List<CachePolicySection> CachePolicies { get; set; } = new();

    public List<RateLimitPolicySection> RateLimitPolicies { get; set; } = new();

    public CertificateSection[] Certificates { get; set; } = Array.Empty<CertificateSection>();

    public LetsEncryptSection? LetsEncrypt { get; set; } = null;

    public CacheSettings? GetCachePolicy(string name)
    {
        _cachePolicies ??= CachePolicies.ToDictionary(p => p.Name, p => p);

        _cachePolicies.TryGetValue(name, out var policy);

        return policy;
    }

    public RateLimitSettings? GetRateLimitPolicy(string name)
    {
        _rateLimitpolicies ??= RateLimitPolicies.ToDictionary(p => p.Name, p => p);

        _rateLimitpolicies.TryGetValue(name, out var policy);

        return policy;
    }
}
