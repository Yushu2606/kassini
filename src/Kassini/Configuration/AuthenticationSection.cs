using Kassini.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;

namespace Kassini.Configuration;

public partial class AuthenticationSection
{
    public string BrowserToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public AuthenticationMode Mode { get; set; } = AuthenticationMode.Unsecured;

    public string? ClientId { get; set; } = null;

    public string? ClientSecret { get; set; } = null;

    public string? Authority { get; set; } = null;

    public string? MetadataAddress { get; set; } = null;

    public string? AuthorizationEndpoint { get; set; } = null;

    public string? TokenEndpoint { get; set; } = null;

    public string? UserInformationEndpoint { get; set; } = null;

    public string? CallbackPath { get; set; } = null;

    public string[] Scopes { get; set; } = [];

    public bool SaveTokens { get; set; } = false;

    public bool GetClaimsFromUserInfoEndpoint { get; set; } = true;

    public string NameClaimType { get; set; } = ClaimTypes.Name;

    public string RoleClaimType { get; set; } = ClaimTypes.Role;

    public Dictionary<string, string> ClaimMappings { get; set; } = [];

    /// <summary>
    /// Gets the optional name of a claim that users authenticated via OpenID Connect are required to have.
    /// If specified, users without this claim will be rejected. If <see cref="RequiredClaimValue"/>
    /// is also specified, then the value of this claim must also match <see cref="RequiredClaimValue"/>.
    /// </summary>
    public string? ClaimType { get; set; } = null;

    /// <summary>
    /// Gets the optional value of the <see cref="RequiredClaimType"/> claim for users authenticated via
    /// OpenID Connect. If specified, users not having this value for the corresponding claim type are
    /// rejected.
    /// </summary>
    public string? ClaimValue { get; set; } = null;

    public string? Role { get; set; } = null;

    public Dictionary<string, PolicySection> Policies { get; set; } = [];
}
