// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Kassini.Configuration;
using Microsoft.AspNetCore.Authorization;

namespace Kassini.Authentication;

internal static class AuthorizationPolicyBuilderExtensions
{
    /// <summary>
    /// Validates that the the expected claim and value are present.
    /// </summary>
    public static AuthorizationPolicyBuilder RequireAuthenticationPolicy(this AuthorizationPolicyBuilder builder, AuthenticationSection options)
    {
        switch (options.Mode)
        {
            case AuthenticationMode.OpenIdConnect:
            case AuthenticationMode.Google:
            case AuthenticationMode.GitHub:
            case AuthenticationMode.OAuth:
                builder.RequirePolicySection(new PolicySection
                {
                    Claims = CreateClaims(options.ClaimType, options.ClaimValue),
                    Roles = string.IsNullOrWhiteSpace(options.Role) ? null : [options.Role]
                });
                break;
            case AuthenticationMode.BrowserToken:
                builder.RequireClaim(AuthorizationDefaults.BrowserTokenClaimName);
                break;
            case AuthenticationMode.Unsecured:
                builder.RequireAssertion(_ => true);
                break;
            default:
                throw new NotSupportedException($"Unexpected {nameof(AuthenticationMode)} enum member: {options.Mode}");
        }

        return builder;
    }

    public static AuthorizationPolicyBuilder RequirePolicySection(this AuthorizationPolicyBuilder builder, PolicySection policy)
    {
        var hasRequirements = false;

        if (policy.Claims is { Count: > 0 })
        {
            foreach (var claim in policy.Claims)
            {
                if (string.IsNullOrWhiteSpace(claim.Value))
                {
                    builder.RequireClaim(claim.Key);
                }
                else
                {
                    builder.RequireClaim(claim.Key, claim.Value);
                }
            }

            hasRequirements = true;
        }

        if (policy.Roles is { Length: > 0 })
        {
            builder.RequireRole(policy.Roles);
            hasRequirements = true;
        }

        if (!hasRequirements)
        {
            builder.RequireAuthenticatedUser();
        }

        return builder;
    }

    private static Dictionary<string, string>? CreateClaims(string? claimType, string? claimValue)
    {
        return string.IsNullOrWhiteSpace(claimType)
            ? null
            : new Dictionary<string, string> { [claimType] = claimValue ?? "" };
    }
}
