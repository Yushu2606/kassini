using Kassini.Configuration;
using LettuceEncrypt;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using System.Net;
using System.Threading.RateLimiting;
using Yarp.ReverseProxy.Configuration;

// Load configuration from arguments

var configFilePath = args.Length > 0 ? args[0] : "simple.yml";

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
        app.UseRouting();
        app.MapReverseProxy();
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
    }

    webApplications.Add(app);
}

Task.WaitAll(webApplications.Select(x => x.RunAsync()).ToArray());
