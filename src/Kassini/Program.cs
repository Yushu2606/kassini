using Kassini.Configuration;
using Kassini.Filters;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using System.Threading.RateLimiting;
using System.Net;
using Yarp.ReverseProxy.Configuration;
using LettuceEncrypt;
using Microsoft.AspNetCore.Rewrite;

// Load configuration from arguments

var configFilePath = args.Length > 0 ? args[0] : "config.yml";
var configurationSource = ConfigurationSource.Parse(configFilePath);

var filtersFactory = new Dictionary<string, Func<FilterSection, IBodyFilter>>();
MarkdownFilter.Register(filtersFactory);
LiquidFilter.Register(filtersFactory);

// TODO: Validation
// - File paths should exist
// - Routes are mandatory
// - Body, File, Files are exclusive

// Build urls from Address values

var webApplications = new List<WebApplication>();

foreach (var server in configurationSource.ConfigurationSection.Servers)
{
    var builder = WebApplication.CreateBuilder(args);

    var hasCachedRoutes = false;
    var hasRateLimitedRoutes = false;
    var hasProxiedRoutes = false;
    var letsEncrypt = configurationSource.ConfigurationSection.LetsEncrypt;

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
                Transforms = new List<IReadOnlyDictionary<string, string>> { transforms }
            };

            // {**catch-all}, 
            // "Path": "/app1/{*any}",

            var cluster = new ClusterConfig() { ClusterId = clusterId, Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase) { { destinationId, new() { Address = endpoint.Proxy.Destination } } } };


            routes.Add(route);
            clusters.Add(cluster);
        }
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

    if (hasProxiedRoutes)
    {
        builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);
    }

    if (hasCachedRoutes)
    {
        builder.Services.AddOutputCache(options =>
        {
            // Pre-defined policies
            foreach (var cacheSection in configurationSource.ConfigurationSection.CachePolicies)
            {
                options.AddPolicy(cacheSection.Name, builder => CreateCachePolicy(builder, cacheSection));
            }

            // Inline policies
            foreach (var endpoint in server.Endpoints)
            {
                var cacheSection = endpoint.Cache;

                if (cacheSection == null || cacheSection.Policy != null)
                {
                    continue;
                }

                cacheSection.Policy = Guid.NewGuid().ToString("n");

                options.AddPolicy(cacheSection.Policy, builder => CreateCachePolicy(builder, cacheSection));
            }

            void CreateCachePolicy(OutputCachePolicyBuilder builder, CacheSettings settings)
            {
                builder.Expire(settings.GetDuration());
            }
        });
    }

    if (hasRateLimitedRoutes)
    {
        builder.Services.AddRateLimiter(options =>
        {
            // Pre-defined policies
            foreach (var rateLimitPolicySection in configurationSource.ConfigurationSection.RateLimitPolicies)
            {
                options.AddPolicy(rateLimitPolicySection.Name, httpContext => CreateRateLimit(rateLimitPolicySection, httpContext));
            }

            // Inline policies
            foreach (var endpoint in server.Endpoints)
            {
                var rateLimitPolicySection = endpoint.RateLimit;

                if (rateLimitPolicySection == null || rateLimitPolicySection.Policy != null)
                {
                    continue;
                }

                rateLimitPolicySection.Policy = Guid.NewGuid().ToString("n");

                options.AddPolicy(rateLimitPolicySection.Policy, httpContext => CreateRateLimit(rateLimitPolicySection, httpContext));
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
            var parts = bind.Address?.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.RemoveEmptyEntries);
            
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
                    else
                    {
                        var certificate = configurationSource.ConfigurationSection.Certificates.First(x => x.Name == bind.Certificate);
                        if (certificate.Path != null)
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

    if (hasProxiedRoutes)
    {
        app.UseRouting();
        app.MapReverseProxy();
    }

    if (server.ResponseCompression != null && server.ResponseCompression.Enabled)
    {
        app.UseResponseCompression();
    }

    foreach (var endpoint in server.Endpoints)
    {
        if (endpoint.Route == null)
        {
            throw new NotSupportedException("Route not defined for endpoint");
        }

        RouteHandlerBuilder routeHandlerBuilder;

        if (endpoint.Body != null)
        {
            routeHandlerBuilder = app.MapGet(endpoint.Route, () => TypedResults.Content(endpoint.Body, endpoint.ContentType));
        }
        else if (endpoint.File != null && endpoint.File.Path != null)
        {
            var content = File.ReadAllBytes(endpoint.File.Path);
            routeHandlerBuilder = app.MapGet(endpoint.Route, () => TypedResults.Bytes(content, endpoint.ContentType));
        }
        else if (endpoint.Redirect != null)
        {
            routeHandlerBuilder = app.MapGet(endpoint.Route, () => TypedResults.Redirect(endpoint.Redirect));
        }
        else if (endpoint.Files != null)
        {
            var filesPath = Path.Combine(builder.Environment.ContentRootPath, endpoint.Files.Path ?? "");
            app.UseStaticFiles(new StaticFileOptions() { RequestPath = endpoint.Route, FileProvider = new PhysicalFileProvider(filesPath) });
            continue;
        }
        else if (endpoint.Proxy != null)
        {
            // Proxied routes are configured using Yarp's configuration model
            // TODO:
            // - How to add Rate limiting or Output caching to these routes?
            continue;
        }
        else
        {
            continue;
        }

        if (endpoint.Status != null)
        {
            routeHandlerBuilder = routeHandlerBuilder.AddEndpointFilter(new StatusCodeFilter(endpoint.Status.Value));
        }

        if (endpoint.Headers != null)
        {
            routeHandlerBuilder = routeHandlerBuilder.AddEndpointFilter(new HeadersFilter(endpoint.Headers));
        }

        if (endpoint.Filters.Any())
        {
            var filters = new List<IBodyFilter>();
            foreach (var filter in endpoint.Filters)
            {
                if (filter == null || filter.Name == null || !filtersFactory.TryGetValue(filter.Name, out var factory))
                {
                    continue;
                }

                filters.Add(factory(filter));
            }

            routeHandlerBuilder.AddEndpointFilter(new FiltersEndpointFilter(filters.ToArray()));
        }

        if (endpoint.Cache != null && endpoint.Cache.Enabled && endpoint.Cache.Policy != null)
        {
            routeHandlerBuilder = routeHandlerBuilder.CacheOutput(endpoint.Cache.Policy);
        }

        if (endpoint.RateLimit != null && endpoint.RateLimit.Enabled && endpoint.RateLimit.Policy != null)
        {
            routeHandlerBuilder = routeHandlerBuilder.RequireRateLimiting(endpoint.RateLimit.Policy);
        }
    }

    webApplications.Add(app);
}

Task.WaitAll(webApplications.Select(x => x.RunAsync()).ToArray());

class StatusCodeFilter : IEndpointFilter
{
    private readonly int _statusCode;

    public StatusCodeFilter(int statusCode)
    {
        _statusCode = statusCode;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);

        context.HttpContext.Response.StatusCode = _statusCode;

        return result;
    }
}

class HeadersFilter : IEndpointFilter
{
    private readonly Dictionary<string, string> _headers;

    public HeadersFilter(Dictionary<string, string> headers)
    {
        _headers = headers;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);

        var responseHeaders = context.HttpContext.Response.Headers;

        foreach (var header in _headers)
        {
            responseHeaders[header.Key] = header.Value;
        }

        return result;
    }
}

class FiltersEndpointFilter : IEndpointFilter
{
    private readonly IBodyFilter[] _filters;

    public FiltersEndpointFilter(IBodyFilter[] filters)
    {
        _filters = filters;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var body = context.HttpContext.Response.Body;
        var dummy = new MemoryStream();
        context.HttpContext.Response.Body = dummy;

        var result = await next(context) as IResult;
        if (result != null) 
        {
            await result.ExecuteAsync(context.HttpContext);
        }

        context.HttpContext.Response.Body = body;

        string? content = null;

        dummy.Seek(0, SeekOrigin.Begin);
        using (var reader = new StreamReader(dummy))
        {
            content = reader.ReadToEnd();
        }

        foreach (var filter in _filters)
        {
            if (filter != null && content != null)
            {
                content = await filter.FilterAsync(content, context.HttpContext.Request.RouteValues);
            }
        }

        result = TypedResults.Content(content);

        dummy.Dispose();

        return result;
    }    
}
