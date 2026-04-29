using Kassini.Authentication;
using Kassini.Configuration;
using LettuceEncrypt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Yarp.ReverseProxy.Configuration;

// Load configuration from arguments

var configFilePath = args.Length > 0 ? args[0] : "auth.yml";

Console.WriteLine($"Loading configuration from {Path.GetFullPath(configFilePath)}");

var configurationSource = ConfigurationSource.Parse(configFilePath);

// TODO: Validation
// - File paths should exist
// - Routes are mandatory
// - Body, File, Files are exclusive

// Build urls from Address values

var webApplications = new List<WebApplication>();

foreach (var server in configurationSource.ConfigurationSection.Servers)
{
    var builder = WebApplication.CreateBuilder();

    var hasCachedRoutes = false;
    var hasRateLimitedRoutes = false;
    var hasProxiedRoutes = false;

    var letsEncrypt = configurationSource.ConfigurationSection.LetsEncrypt;

    // Yarp's configuration is imported from each endpoint's proxy property and also the server's reverseProxy one
    var routes = new List<RouteConfig>();
    var clusters = new List<ClusterConfig>();

    // Build proxy configurations
    foreach (var endpoint in server.Endpoints)
    {
        // Detects if at least one route uses caching and we need to register the middleware
        if (endpoint.Cache != null)
        {
            hasCachedRoutes = true;
        }

        // Detects if at least one route uses rate limiting and we need to register the middleware
        if (endpoint.RateLimit != null)
        {
            hasRateLimitedRoutes = true;
        }

        if (endpoint.Proxy != null && endpoint.Proxy.Destination != null && endpoint.Route != null)
        {
            hasProxiedRoutes = true;

            var routeId = Guid.NewGuid().ToString();
            var clusterId = Guid.NewGuid().ToString();
            var destinationId = Guid.NewGuid().ToString();

            var pattern = endpoint.Route;
            var prefix = endpoint.Route;

            if (pattern == "*")
            {
                prefix = "";
                pattern = "{**catch-all}";
            }
            else if (pattern.EndsWith("/*"))
            {
                prefix = pattern[..^2];
                pattern = prefix + "/{*any}";
            }

            // TODO:
            // - There should be an option to rewrite the proxies content to change the urls when there is a prefix in the url

            var transforms = new Dictionary<string, string>();

            if (endpoint.Proxy.RemovePrefix)
            {
                transforms["PathRemovePrefix"] = prefix;
            }

            var route = new RouteConfig
            {
                RouteId = routeId,
                ClusterId = clusterId,
                Match = new RouteMatch { Path = pattern }, // , Hosts = server.Hosts.Select(x => x.ToString()).ToArray()
                AuthorizationPolicy = endpoint.Policy,
                Transforms = [transforms]
            };

            // {**catch-all},
            // "Path": "/app1/{*any}",

            var cluster = new ClusterConfig() { ClusterId = clusterId, Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase) { { destinationId, new() { Address = endpoint.Proxy.Destination } } } };


            routes.Add(route);
            clusters.Add(cluster);
        }
    }

    if (server.ReverseProxy != null)
    {
        routes.AddRange(server.ReverseProxy.Routes);
        clusters.AddRange(server.ReverseProxy.Clusters);
    }

    if (letsEncrypt != null && letsEncrypt.Email != null && letsEncrypt.Domains.Any() && server.Bind.Any(x => x.Certificate == "letsencrypt"))
    {
        builder.Services.AddLettuceEncrypt(options =>
        {
            options.AcceptTermsOfService = true;
            options.EmailAddress = letsEncrypt.Email;
            options.DomainNames = letsEncrypt.Domains;
            options.UseStagingServer = true;
        })
        .PersistDataToDirectory(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, letsEncrypt.Path)), null);
    }

    if (server.HttpsRedirection != null && server.HttpsRedirection.Enabled)
    {
        builder.Services.AddHttpsRedirection(options =>
        {
            options.RedirectStatusCode = StatusCodes.Status307TemporaryRedirect;
            options.HttpsPort = server.HttpsRedirection.Port ?? 443;
        });
    }

    if (server.ResponseCompression != null && server.ResponseCompression.Enabled)
    {
        builder.Services.AddResponseCompression();
    }

    if (hasProxiedRoutes || server.ReverseProxy != null)
    {
        builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);
    }

    if (hasCachedRoutes)
    {
        // Create inline policies
        foreach (var endpoint in server.Endpoints)
        {
            var cacheSection = endpoint.Cache;

            if (cacheSection == null || cacheSection.Policy != null)
            {
                continue;
            }

            cacheSection.Policy = Guid.NewGuid().ToString("n");

            configurationSource.ConfigurationSection.CachePolicies.Add(new CachePolicySection
            {
                Name = cacheSection.Policy,
                Duration = cacheSection.Duration,
            });
        }

        builder.Services.AddOutputCache(options =>
        {
            // Pre-defined policies
            foreach (var cacheSection in configurationSource.ConfigurationSection.CachePolicies)
            {
                options.AddPolicy(cacheSection.Name, builder => CreateCachePolicy(builder, cacheSection));
            }

            void CreateCachePolicy(OutputCachePolicyBuilder builder, CacheSettings settings)
            {
                builder.Expire(settings.GetDuration());
            }
        });
    }

    if (hasRateLimitedRoutes)
    {
        // Create inline policies
        foreach (var endpoint in server.Endpoints)
        {
            var rateLimitPolicySection = endpoint.RateLimit;

            if (rateLimitPolicySection == null || rateLimitPolicySection.Policy != null)
            {
                continue;
            }

            rateLimitPolicySection.Policy = Guid.NewGuid().ToString("n");

            configurationSource.ConfigurationSection.RateLimitPolicies.Add(new RateLimitPolicySection
            {
                Name = rateLimitPolicySection.Policy,
                Duration = rateLimitPolicySection.Duration,
            });
        }

        builder.Services.AddRateLimiter(options =>
        {
            // Pre-defined policies
            foreach (var rateLimitPolicySection in configurationSource.ConfigurationSection.RateLimitPolicies)
            {
                options.AddPolicy(rateLimitPolicySection.Name, httpContext => CreateRateLimit(rateLimitPolicySection, httpContext));
            }

            RateLimitPartition<string> CreateRateLimit(RateLimitSettings settings, HttpContext httpContext)
            {
                var partition = settings.Partition switch
                {
                    Partitions.None => "",
                    Partitions.ConnectionId => httpContext.Connection.Id,
                    Partitions.User => httpContext?.User?.Identity?.Name ?? "",
                    Partitions.IpAddress => httpContext?.Connection?.RemoteIpAddress?.ToString() ?? "",
                    _ => ""
                };

                return RateLimitPartition.GetFixedWindowLimiter(
                    partition,
                    key => new FixedWindowRateLimiterOptions()
                    {
                        Window = settings.GetDuration(),
                        PermitLimit = settings.Permit ?? 1,
                        QueueLimit = settings.Queue ?? 0
                    }
                );
            }
        });
    }

    // Configure telemetry and health checks
    builder.AddServiceDefaults();

    builder.Services.AddOpenTelemetry().WithTracing(tracerProviderBuilder =>
    {
        tracerProviderBuilder.AddSource("Yarp.ReverseProxy");
    });

    var authenticationScheme = "kassini";

    if (server.Authentication != null)
    {
        var authentication = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme);
        ConfigureAuthentication(authentication, server.Authentication, authenticationScheme);

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationDefaults.PolicyName, CreateAuthenticationPolicy());

            foreach (var policy in server.Authentication.Policies)
            {
                options.AddPolicy(policy.Key, CreateAuthenticationPolicy(policy.Value));
            }

            AuthorizationPolicy CreateAuthenticationPolicy(PolicySection? policySection = null)
            {
                var policyBuilder = server.Authentication.Mode == AuthenticationMode.Unsecured
                    ? new AuthorizationPolicyBuilder()
                    : new AuthorizationPolicyBuilder(authenticationScheme);

                policyBuilder.RequireAuthenticationPolicy(server.Authentication);

                if (policySection != null)
                {
                    policyBuilder.RequirePolicySection(policySection);
                }

                return policyBuilder.Build();
            }

        });
    }
    else
    {
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(
                name: AuthorizationDefaults.PolicyName,
                policy: new AuthorizationPolicyBuilder()
                    .RequireAssertion(_ =>
                    {
                        // our policy doesn't require anything.
                        return true;
                    })
                    .Build());
        });
    }

    // TODO: Convert this to IOptions
    builder.Services.AddSingleton(server);

    builder.WebHost.ConfigureKestrel((context, options) =>
    {
        // Default to http1 and http2 if no protocols are specified
        var protocols = HttpProtocols.Http1AndHttp2;

        foreach (var bind in server.Bind)
        {
            if (bind.Protocols.Any())
            {
                protocols = HttpProtocols.None;

                foreach (var p in bind.Protocols)
                {
                    if (Enum.TryParse<HttpProtocols>(p, true, out var protocol))
                    {
                        protocols |= protocol;
                    }
                }
            }

            var address = IPAddress.Any;
            var port = 80;

            // [address][:port(80)]
            var parts = bind.Address?.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.RemoveEmptyEntries);

            switch (parts?.Length)
            {
                case 1: if (!int.TryParse(parts[0], out port)) IPAddress.TryParse(parts[0], out address); port = 80; break;
                case 2: IPAddress.TryParse(parts[0], out address); if (!int.TryParse(parts[1], out port)) port = 80; break;
                default: break;
            }

            options.Listen(address ?? IPAddress.Any, port, listenOptions =>
            {
                listenOptions.Protocols = protocols;
                if (bind.Certificate != null)
                {
                    if (bind.Certificate == "letsencrypt")
                    {
                        listenOptions.UseHttps(https =>
                        {
                            https.UseLettuceEncrypt(options.ApplicationServices);
                        });
                    }
                    if (bind.Certificate == "dev")
                    {
                        listenOptions.UseHttps(https =>
                        {
                        });
                    }
                    else
                    {
                        var certificate = configurationSource.ConfigurationSection.Certificates.First(x => x.Name == bind.Certificate);
                        if (!string.IsNullOrWhiteSpace(certificate.Path))
                        {
                            listenOptions.UseHttps(certificate.Path, certificate.Password);
                        }
                    }
                }
            });
        }
    });

    var app = builder.Build();

    if (server.HttpsRedirection != null && server.HttpsRedirection.Enabled)
    {
        app.UseHttpsRedirection();
    }

    if (hasProxiedRoutes || server.ReverseProxy != null)
    {
        app.UseRouting();
    }

    if (server.Authentication != null)
    {
        if (server.Authentication.Mode == AuthenticationMode.BrowserToken)
        {
            app.UseMiddleware<ValidateTokenMiddleware>();
        }

        app.UseAuthentication();
        app.UseAuthorization();

        if (server.Authentication.Mode == AuthenticationMode.BrowserToken)
        {
            app.MapGet("/login", (HttpContext httpContext) =>
            {
                var returnUrl = httpContext.Request.Query[CookieAuthenticationDefaults.ReturnUrlParameter].ToString();
                var action = "/api/validatetoken";

                if (!string.IsNullOrWhiteSpace(returnUrl))
                {
                    action += $"?{CookieAuthenticationDefaults.ReturnUrlParameter}={Uri.EscapeDataString(returnUrl)}";
                }

                var html = $"""
                    <form method="post" action="{WebUtility.HtmlEncode(action)}">
                        <label>Token <input name="token" type="password" autocomplete="current-password" /></label>
                        <button type="submit">Sign in</button>
                    </form>
                    """;

                return Results.Content(html, contentType: "text/html");
            });

            app.MapPost("/api/validatetoken", async (HttpContext httpContext) =>
            {
                var token = httpContext.Request.Query["token"].ToString();

                if (string.IsNullOrEmpty(token) && httpContext.Request.HasFormContentType)
                {
                    var form = await httpContext.Request.ReadFormAsync().ConfigureAwait(false);
                    token = form["token"].ToString();
                }

                if (await ValidateTokenMiddleware.TryAuthenticateAsync(token, httpContext, server.Authentication.BrowserToken).ConfigureAwait(false))
                {
                    var returnUrl = httpContext.Request.Query[CookieAuthenticationDefaults.ReturnUrlParameter].ToString();
                    return Results.Redirect(ValidateTokenMiddleware.GetSafeReturnUrl(returnUrl));
                }

                var safeReturnUrl = ValidateTokenMiddleware.GetSafeReturnUrl(
                    httpContext.Request.Query[CookieAuthenticationDefaults.ReturnUrlParameter].ToString());

                return Results.Redirect($"/login?{CookieAuthenticationDefaults.ReturnUrlParameter}={Uri.EscapeDataString(safeReturnUrl)}");
            });
        }
    }

    if (server.Redirect.Any() || server.Rewrite.Any())
    {
        var options = new RewriteOptions();
        foreach (var redirect in server.Redirect)
        {
            if (redirect.From != null && redirect.To != null)
            {
                options.AddRedirect(redirect.From, redirect.To);
            }
        }

        foreach (var rewrite in server.Rewrite)
        {
            if (rewrite.From != null && rewrite.To != null)
            {
                options.AddRewrite(rewrite.From, rewrite.To, rewrite.SkipRemainingRules);
            }
        }

        app.UseRewriter(options);
    }

    if (hasCachedRoutes)
    {
        app.UseOutputCache();
    }

    if (hasRateLimitedRoutes)
    {
        app.UseRateLimiter();
    }

    if (hasProxiedRoutes || server.ReverseProxy != null)
    {
        var reverseProxy = app.MapReverseProxy();

        if (server.ReverseProxy?.Policy is { } policy)
        {
            reverseProxy.RequireAuthorization(policy);
        }
    }

    if (server.ResponseCompression != null && server.ResponseCompression.Enabled)
    {
        app.UseResponseCompression();
    }

    Dictionary<string, IEndpointRouteBuilder> endpointGroups = [];

    // Process all route groups (Route ends with '/*')
    foreach (var endpoint in server.Endpoints)
    {
        if (endpoint.Route == null || !endpoint.Route.EndsWith("/*"))
        {
            continue;
        }

        var newRoute = endpoint.Route[..^2];

        if (newRoute == "")
        {
            newRoute = "/";
        }

        var routeGroupBuilder = app.MapGroup(newRoute);

        endpointGroups[newRoute] = routeGroupBuilder;
    }

    foreach (var endpoint in server.Endpoints)
    {
        if (endpoint.Route == null)
        {
            throw new NotSupportedException("Route not defined for endpoint");
        }

        IEndpointRouteBuilder routeGroupBuilder = app;
        IEndpointConventionBuilder? routeHandlerBuilder = null;

        foreach (var group in endpointGroups)
        {
            if (endpoint.Route.StartsWith(group.Key, StringComparison.OrdinalIgnoreCase))
            {
                routeGroupBuilder = group.Value;
            }
        }

        if (!endpoint.Route.EndsWith("/*"))
        {
            if (endpoint.Body != null)
            {
                routeHandlerBuilder = routeGroupBuilder.MapMethods(endpoint.Route, endpoint.GetMethods(), () => TypedResults.Content(endpoint.Body, endpoint.ContentType));
            }
            else if (endpoint.File != null && endpoint.File.Path != null)
            {
                // Just for demonstrations purpose, not production-ready
                routeHandlerBuilder = routeGroupBuilder.MapMethods(endpoint.Route, endpoint.GetMethods(), () => TypedResults.PhysicalFile(Path.GetFullPath(endpoint.File.Path), endpoint.ContentType));
            }
            else if (endpoint.Redirect != null)
            {
                routeHandlerBuilder = routeGroupBuilder.MapMethods(endpoint.Route, endpoint.GetMethods(), () => TypedResults.Redirect(endpoint.Redirect));
            }
            else if (endpoint.Files != null)
            {
                var filesPath = Path.Combine(builder.Environment.ContentRootPath, endpoint.Files.Path ?? "");

                var contentTypeProvider = new FileExtensionContentTypeProvider();

                if (endpoint.Files.MimeTypes != null && endpoint.Files.MimeTypes.Count != 0)
                {
                    contentTypeProvider = new();

                    foreach (var contentType in endpoint.Files.MimeTypes)
                    {
                        contentTypeProvider.Mappings[contentType.Key] = contentType.Value;
                    }
                }

                app.UseStaticFiles(new StaticFileOptions() { RequestPath = endpoint.Route, FileProvider = new PhysicalFileProvider(filesPath), ContentTypeProvider = contentTypeProvider });
                continue;
            }
            else if (endpoint.Proxy != null)
            {
                // Proxied routes are configured using Yarp's configuration model
                // TODO:
                // - How to add Rate limiting or Output caching to these routes?
                continue;
            }
        }
        else
        {
            routeHandlerBuilder = (RouteGroupBuilder)routeGroupBuilder;
        }

        if (endpoint.Status != null)
        {
            routeHandlerBuilder = routeHandlerBuilder!.AddEndpointFilter(new StatusCodeFilter(endpoint.Status.Value));
        }

        if (endpoint.Headers != null)
        {
            routeHandlerBuilder = routeHandlerBuilder!.AddEndpointFilter(new HeadersFilter(endpoint.Headers));
        }

        if (endpoint.Cache != null && endpoint.Cache.Enabled && endpoint.Cache.Policy != null)
        {
            routeHandlerBuilder = routeHandlerBuilder!.CacheOutput(endpoint.Cache.Policy);
        }

        if (endpoint.RateLimit != null && endpoint.RateLimit.Enabled && endpoint.RateLimit.Policy != null)
        {
            routeHandlerBuilder = routeHandlerBuilder!.RequireRateLimiting(endpoint.RateLimit.Policy);
        }

        if (endpoint.Policy != null)
        {
            routeHandlerBuilder = routeHandlerBuilder!.RequireAuthorization(endpoint.Policy);
        }
    }

    webApplications.Add(app);
}

Task.WaitAll(webApplications.Select(x => x.RunAsync()).ToArray());

static void ConfigureAuthentication(AuthenticationBuilder authentication, AuthenticationSection authenticationSection, string authenticationScheme)
{
    var challengeScheme = authenticationSection.Mode switch
    {
        AuthenticationMode.OpenIdConnect => OpenIdConnectDefaults.AuthenticationScheme,
        AuthenticationMode.Google => GoogleDefaults.AuthenticationScheme,
        AuthenticationMode.GitHub => "GitHub",
        AuthenticationMode.OAuth => "OAuth",
        _ => CookieAuthenticationDefaults.AuthenticationScheme
    };

    authentication.AddPolicyScheme(authenticationScheme, displayName: authenticationScheme, options =>
    {
        options.ForwardDefault = CookieAuthenticationDefaults.AuthenticationScheme;

        if (IsExternalAuthenticationMode(authenticationSection.Mode))
        {
            options.ForwardChallenge = challengeScheme;
        }
    });

    authentication.AddCookie(options =>
    {
        if (authenticationSection.Mode == AuthenticationMode.BrowserToken)
        {
            options.LoginPath = "/login";
            options.ReturnUrlParameter = "returnUrl";
            options.ExpireTimeSpan = TimeSpan.FromDays(3);
            options.Events.OnSigningIn = context =>
            {
                // This distinguishes browser-token cookies from cookies issued by external providers.
                var claimsIdentity = (ClaimsIdentity)context.Principal!.Identity!;
                claimsIdentity.AddClaim(new Claim(AuthorizationDefaults.BrowserTokenClaimName, bool.TrueString));
                return Task.CompletedTask;
            };
        }
    });

    switch (authenticationSection.Mode)
    {
        case AuthenticationMode.OpenIdConnect:
            ValidateClientCredentials(authenticationSection);
            ValidateOpenIdConnectConfiguration(authenticationSection);
            authentication.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options => ConfigureOpenIdConnect(options, authenticationSection));
            break;
        case AuthenticationMode.Google:
            ValidateClientCredentials(authenticationSection);
            authentication.AddGoogle(GoogleDefaults.AuthenticationScheme, options => ConfigureGoogle(options, authenticationSection));
            break;
        case AuthenticationMode.GitHub:
            ValidateClientCredentials(authenticationSection);
            authentication.AddOAuth("GitHub", options => ConfigureGitHub(options, authenticationSection));
            break;
        case AuthenticationMode.OAuth:
            ValidateClientCredentials(authenticationSection);
            ValidateOAuthConfiguration(authenticationSection);
            authentication.AddOAuth("OAuth", options => ConfigureOAuth(options, authenticationSection, defaultScopes: []));
            break;
        case AuthenticationMode.BrowserToken:
        case AuthenticationMode.Unsecured:
            break;
        default:
            throw new NotSupportedException($"Unexpected {nameof(AuthenticationMode)} enum member: {authenticationSection.Mode}");
    }
}

static void ConfigureOpenIdConnect(OpenIdConnectOptions options, AuthenticationSection authenticationSection)
{
    options.ClientId = authenticationSection.ClientId;
    options.ClientSecret = authenticationSection.ClientSecret;
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.CallbackPath = authenticationSection.CallbackPath ?? "/signin-oidc";
    options.SaveTokens = authenticationSection.SaveTokens;
    options.GetClaimsFromUserInfoEndpoint = authenticationSection.GetClaimsFromUserInfoEndpoint;
    options.TokenValidationParameters.NameClaimType = authenticationSection.NameClaimType;
    options.TokenValidationParameters.RoleClaimType = authenticationSection.RoleClaimType;

    if (!string.IsNullOrWhiteSpace(authenticationSection.Authority))
    {
        options.Authority = authenticationSection.Authority;
    }

    if (!string.IsNullOrWhiteSpace(authenticationSection.MetadataAddress))
    {
        options.MetadataAddress = authenticationSection.MetadataAddress;
    }

    AddScopes(options.Scope, [OpenIdConnectScope.OpenId, "profile"], authenticationSection.Scopes);
}

static void ConfigureGoogle(GoogleOptions options, AuthenticationSection authenticationSection)
{
    options.ClientId = authenticationSection.ClientId!;
    options.ClientSecret = authenticationSection.ClientSecret!;
    options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.CallbackPath = authenticationSection.CallbackPath ?? "/signin-google";
    options.SaveTokens = authenticationSection.SaveTokens;

    AddScopes(options.Scope, defaultScopes: [], authenticationSection.Scopes);
}

static void ConfigureGitHub(OAuthOptions options, AuthenticationSection authenticationSection)
{
    ConfigureOAuth(options, authenticationSection, ["read:user", "user:email"], mapClaims: authenticationSection.ClaimMappings.Count > 0);

    options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
    options.TokenEndpoint = "https://github.com/login/oauth/access_token";
    options.UserInformationEndpoint = "https://api.github.com/user";
    options.CallbackPath = authenticationSection.CallbackPath ?? "/signin-github";

    if (authenticationSection.ClaimMappings.Count == 0)
    {
        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");
        options.ClaimActions.MapJsonKey("urn:github:name", "name");
        options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
    }
}

static void ConfigureOAuth(OAuthOptions options, AuthenticationSection authenticationSection, string[] defaultScopes, bool mapClaims = true)
{
    options.ClientId = authenticationSection.ClientId!;
    options.ClientSecret = authenticationSection.ClientSecret!;
    options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.AuthorizationEndpoint = authenticationSection.AuthorizationEndpoint ?? options.AuthorizationEndpoint;
    options.TokenEndpoint = authenticationSection.TokenEndpoint ?? options.TokenEndpoint;
    options.UserInformationEndpoint = authenticationSection.UserInformationEndpoint ?? options.UserInformationEndpoint;
    options.CallbackPath = authenticationSection.CallbackPath ?? "/signin-oauth";
    options.SaveTokens = authenticationSection.SaveTokens;

    AddScopes(options.Scope, defaultScopes, authenticationSection.Scopes);
    if (mapClaims)
    {
        MapOAuthClaims(options, authenticationSection);
    }

    options.Events.OnCreatingTicket = async context =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.UserAgent.ParseAdd("Kassini");

        using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted).ConfigureAwait(false));
        context.RunClaimActions(payload.RootElement);
    };
}

static void MapOAuthClaims(OAuthOptions options, AuthenticationSection authenticationSection)
{
    if (authenticationSection.ClaimMappings.Count > 0)
    {
        foreach (var claimMapping in authenticationSection.ClaimMappings)
        {
            options.ClaimActions.MapJsonKey(claimMapping.Key, claimMapping.Value);
        }
    }
    else
    {
        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
        options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
    }
}

static void AddScopes(ICollection<string> scopes, string[] defaultScopes, string[] configuredScopes)
{
    foreach (var scope in defaultScopes.Concat(configuredScopes))
    {
        if (!scopes.Contains(scope))
        {
            scopes.Add(scope);
        }
    }
}

static bool IsExternalAuthenticationMode(AuthenticationMode mode)
{
    return mode is AuthenticationMode.OpenIdConnect or AuthenticationMode.Google or AuthenticationMode.GitHub or AuthenticationMode.OAuth;
}

static void ValidateClientCredentials(AuthenticationSection authenticationSection)
{
    if (string.IsNullOrWhiteSpace(authenticationSection.ClientId))
    {
        throw new InvalidOperationException($"Authentication mode '{authenticationSection.Mode}' requires a non-empty clientId.");
    }

    if (string.IsNullOrWhiteSpace(authenticationSection.ClientSecret))
    {
        throw new InvalidOperationException($"Authentication mode '{authenticationSection.Mode}' requires a non-empty clientSecret.");
    }
}

static void ValidateOpenIdConnectConfiguration(AuthenticationSection authenticationSection)
{
    if (string.IsNullOrWhiteSpace(authenticationSection.Authority) && string.IsNullOrWhiteSpace(authenticationSection.MetadataAddress))
    {
        throw new InvalidOperationException("OpenIdConnect authentication requires authority or metadataAddress.");
    }
}

static void ValidateOAuthConfiguration(AuthenticationSection authenticationSection)
{
    if (string.IsNullOrWhiteSpace(authenticationSection.AuthorizationEndpoint))
    {
        throw new InvalidOperationException("OAuth authentication requires authorizationEndpoint.");
    }

    if (string.IsNullOrWhiteSpace(authenticationSection.TokenEndpoint))
    {
        throw new InvalidOperationException("OAuth authentication requires tokenEndpoint.");
    }

    if (string.IsNullOrWhiteSpace(authenticationSection.UserInformationEndpoint))
    {
        throw new InvalidOperationException("OAuth authentication requires userInformationEndpoint.");
    }
}
