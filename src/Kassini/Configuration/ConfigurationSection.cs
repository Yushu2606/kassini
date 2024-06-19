namespace Kassini.Configuration;

public class ConfigurationSection
{
    private Dictionary<string, CachePolicySection>? _cachePolicies;
    private Dictionary<string, RateLimitPolicySection>? _rateLimitPolicies;

    public ServerSection[] Servers { get; set; } = [];

    public List<CachePolicySection> CachePolicies { get; set; } = [];

    public List<RateLimitPolicySection> RateLimitPolicies { get; set; } = [];

    public CertificateSection[] Certificates { get; set; } = [];

    public LetsEncryptSection? LetsEncrypt { get; set; } = null;

    public CacheSettings? GetCachePolicy(string name)
    {
        _cachePolicies ??= CachePolicies.ToDictionary(p => p.Name, p => p);

        _cachePolicies.TryGetValue(name, out var policy);

        return policy;
    }

    public RateLimitSettings? GetRateLimitPolicy(string name)
    {
        _rateLimitPolicies ??= RateLimitPolicies.ToDictionary(p => p.Name, p => p);

        _rateLimitPolicies.TryGetValue(name, out var policy);

        return policy;
    }
}
