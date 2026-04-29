using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TUnit.Core;

namespace Kassini.FunctionalTests;

public sealed class KassiniFunctionalTests
{
    [Test]
    public async Task BodyEndpointsApplyMethodsHeadersStatusAndContentType()
    {
        var port = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              endpoints:
              - route: /hello
                body: Hello from Kassini
                contentType: text/plain
                status: 202
                headers:
                  X-Kassini: functional
              - route: /method
                body: From GET
                contentType: text/plain
              - route: /method
                body: From POST
                contentType: text/plain
                methods: POST
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient();

        using var helloResponse = await httpClient.GetAsync(app.Url("/hello"));
        RequireEqual(HttpStatusCode.Accepted, helloResponse.StatusCode);
        RequireEqual("text/plain", helloResponse.Content.Headers.ContentType?.MediaType);
        RequireTrue(helloResponse.Headers.TryGetValues("X-Kassini", out var headerValues), "Expected X-Kassini response header.");
        RequireEqual("functional", headerValues!.Single());
        RequireEqual("Hello from Kassini", await helloResponse.Content.ReadAsStringAsync());

        using var getResponse = await httpClient.GetAsync(app.Url("/method"));
        RequireEqual(HttpStatusCode.OK, getResponse.StatusCode);
        RequireEqual("From GET", await getResponse.Content.ReadAsStringAsync());

        using var postResponse = await httpClient.PostAsync(app.Url("/method"), null);
        RequireEqual(HttpStatusCode.OK, postResponse.StatusCode);
        RequireEqual("From POST", await postResponse.Content.ReadAsStringAsync());

        using var putResponse = await httpClient.PutAsync(app.Url("/method"), null);
        RequireEqual(HttpStatusCode.MethodNotAllowed, putResponse.StatusCode);
    }

    [Test]
    public async Task FileEndpointAndStaticFilesServeConfiguredContent()
    {
        var port = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              endpoints:
              - route: /file
                file:
                  path: content/message.txt
                contentType: text/plain
              - route: /static
                files:
                  path: public
                  mimeTypes:
                    .cast: application/x-cast
            """;

        await using var app = await KassiniApplication.StartAsync(
            port,
            config,
            new Dictionary<string, string>
            {
                ["content/message.txt"] = "File endpoint payload",
                ["public/asset.cast"] = "Static asset payload"
            });

        using var httpClient = CreateHttpClient();

        using var fileResponse = await httpClient.GetAsync(app.Url("/file"));
        RequireEqual(HttpStatusCode.OK, fileResponse.StatusCode);
        RequireEqual("text/plain", fileResponse.Content.Headers.ContentType?.MediaType);
        RequireEqual("File endpoint payload", await fileResponse.Content.ReadAsStringAsync());

        using var staticResponse = await httpClient.GetAsync(app.Url("/static/asset.cast"));
        RequireEqual(HttpStatusCode.OK, staticResponse.StatusCode);
        RequireEqual("application/x-cast", staticResponse.Content.Headers.ContentType?.MediaType);
        RequireEqual("Static asset payload", await staticResponse.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task RedirectAndRewriteRulesTransformRequests()
    {
        var port = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              rewrite:
              - from: ^rewrite-me$
                to: rewritten
              redirect:
              - from: ^redirect-me/(.*)
                to: redirected/$1
              endpoints:
              - route: /rewritten
                body: Rewritten response
                contentType: text/plain
              - route: /redirected/{value}
                body: Redirect target
                contentType: text/plain
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient(followRedirects: false);

        using var rewriteResponse = await httpClient.GetAsync(app.Url("/rewrite-me"));
        RequireEqual(HttpStatusCode.OK, rewriteResponse.StatusCode);
        RequireEqual("Rewritten response", await rewriteResponse.Content.ReadAsStringAsync());

        using var redirectResponse = await httpClient.GetAsync(app.Url("/redirect-me/value"));
        RequireEqual(HttpStatusCode.Found, redirectResponse.StatusCode);
        RequireEndsWith("/redirected/value", redirectResponse.Headers.Location?.ToString());
    }

    [Test]
    public async Task EndpointRedirectReturnsConfiguredLocation()
    {
        var port = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              endpoints:
              - route: /old
                redirect: /new
              - route: /new
                body: New location
                contentType: text/plain
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient(followRedirects: false);

        using var response = await httpClient.GetAsync(app.Url("/old"));
        RequireEqual(HttpStatusCode.Found, response.StatusCode);
        RequireEqual("/new", response.Headers.Location?.ToString());
    }

    [Test]
    public async Task HttpsRedirectionReturnsTemporaryRedirectToConfiguredPort()
    {
        var port = GetFreePort();
        var httpsPort = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              httpsRedirection:
                enabled: true
                port: {{httpsPort}}
              endpoints:
              - route: /secure
                body: Secure location
                contentType: text/plain
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient(followRedirects: false);

        using var response = await httpClient.GetAsync(app.Url("/secure?x=1"));
        RequireEqual(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        RequireEqual($"https://127.0.0.1:{httpsPort}/secure?x=1", response.Headers.Location?.ToString());
    }

    [Test]
    public async Task RouteGroupAppliesConfiguredFiltersToMatchedEndpoints()
    {
        var port = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              endpoints:
              - route: /*
                status: 201
                headers:
                  X-Group: applied
              - route: /grouped
                body: Grouped response
                contentType: text/plain
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient();

        using var response = await httpClient.GetAsync(app.Url("/grouped"));
        RequireEqual(HttpStatusCode.Created, response.StatusCode);
        RequireTrue(response.Headers.TryGetValues("X-Group", out var headerValues), "Expected X-Group response header.");
        RequireEqual("applied", headerValues!.Single());
        RequireEqual("Grouped response", await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task MultipleServersStartIndependentListeners()
    {
        var firstPort = GetFreePort();
        var secondPort = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{firstPort}}
              endpoints:
              - route: /
                body: First server
                contentType: text/plain
            - bind:
              - address: 127.0.0.1:{{secondPort}}
              endpoints:
              - route: /
                body: Second server
                contentType: text/plain
            """;

        await using var app = await KassiniApplication.StartAsync([firstPort, secondPort], config);
        using var httpClient = CreateHttpClient();

        using var firstResponse = await httpClient.GetAsync(app.Url(firstPort, "/"));
        RequireEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        RequireEqual("First server", await firstResponse.Content.ReadAsStringAsync());

        using var secondResponse = await httpClient.GetAsync(app.Url(secondPort, "/"));
        RequireEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        RequireEqual("Second server", await secondResponse.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task OutputCacheReturnsCachedResponsesWithinConfiguredDuration()
    {
        var port = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              endpoints:
              - route: /cached
                file:
                  path: content/cached.txt
                contentType: text/plain
                cache:
                  duration: 30s
              - route: /cached-with-policy
                file:
                  path: content/cached-with-policy.txt
                contentType: text/plain
                cache:
                  policy: file-cache
            cachePolicies:
            - name: file-cache
              duration: 30s
            """;

        await using var app = await KassiniApplication.StartAsync(
            port,
            config,
            new Dictionary<string, string>
            {
                ["content/cached.txt"] = "first version",
                ["content/cached-with-policy.txt"] = "first policy version"
            });

        using var httpClient = CreateHttpClient();

        using var firstResponse = await httpClient.GetAsync(app.Url("/cached"));
        RequireEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        RequireEqual("first version", await firstResponse.Content.ReadAsStringAsync());

        File.WriteAllText(app.GetWorkspacePath("content/cached.txt"), "second version");

        using var secondResponse = await httpClient.GetAsync(app.Url("/cached"));
        RequireEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        RequireEqual("first version", await secondResponse.Content.ReadAsStringAsync());

        using var firstPolicyResponse = await httpClient.GetAsync(app.Url("/cached-with-policy"));
        RequireEqual(HttpStatusCode.OK, firstPolicyResponse.StatusCode);
        RequireEqual("first policy version", await firstPolicyResponse.Content.ReadAsStringAsync());

        File.WriteAllText(app.GetWorkspacePath("content/cached-with-policy.txt"), "second policy version");

        using var secondPolicyResponse = await httpClient.GetAsync(app.Url("/cached-with-policy"));
        RequireEqual(HttpStatusCode.OK, secondPolicyResponse.StatusCode);
        RequireEqual("first policy version", await secondPolicyResponse.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task RateLimitRejectsRequestsAfterPermitIsConsumed()
    {
        var port = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              endpoints:
              - route: /limited
                body: Limited response
                contentType: text/plain
                rateLimit:
                  duration: 30s
                  permit: 1
                  queue: 0
              - route: /limited-with-policy
                body: Limited policy response
                contentType: text/plain
                rateLimit:
                  policy: one-request
            rateLimitPolicies:
            - name: one-request
              duration: 30s
              permit: 1
              queue: 0
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient();

        using var firstResponse = await httpClient.GetAsync(app.Url("/limited"));
        RequireEqual(HttpStatusCode.OK, firstResponse.StatusCode);

        using var secondResponse = await httpClient.GetAsync(app.Url("/limited"));
        RequireEqual(HttpStatusCode.ServiceUnavailable, secondResponse.StatusCode);

        using var firstPolicyResponse = await httpClient.GetAsync(app.Url("/limited-with-policy"));
        RequireEqual(HttpStatusCode.OK, firstPolicyResponse.StatusCode);

        using var secondPolicyResponse = await httpClient.GetAsync(app.Url("/limited-with-policy"));
        RequireEqual(HttpStatusCode.ServiceUnavailable, secondPolicyResponse.StatusCode);
    }

    [Test]
    public async Task ResponseCompressionCompressesEligibleResponses()
    {
        var port = GetFreePort();
        var responseBody = new string('A', 4096);
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              responseCompression:
                enabled: true
              endpoints:
              - route: /compressed
                body: {{responseBody}}
                contentType: text/plain
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, app.Url("/compressed"));
        request.Headers.AcceptEncoding.ParseAdd("gzip");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        RequireEqual(HttpStatusCode.OK, response.StatusCode);
        RequireTrue(response.Content.Headers.ContentEncoding.Contains("gzip"), "Expected gzip content encoding.");

        await using var compressedStream = await response.Content.ReadAsStreamAsync();
        await using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream);
        RequireEqual(responseBody, await reader.ReadToEndAsync());
    }

    [Test]
    public async Task EndpointProxyForwardsRequestsAndRemovesConfiguredPrefix()
    {
        var port = GetFreePort();
        await using var upstream = new UpstreamServer();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              endpoints:
              - route: /proxy/*
                proxy:
                  destination: {{upstream.Url}}
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient();

        using var response = await httpClient.GetAsync(app.Url("/proxy/downstream?x=1"));
        RequireEqual(HttpStatusCode.OK, response.StatusCode);
        RequireEqual("upstream:/downstream?x=1", await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task ReverseProxyConfigurationForwardsRequests()
    {
        var port = GetFreePort();
        await using var upstream = new UpstreamServer();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              reverseProxy:
                routes:
                - routeId: route1
                  clusterId: cluster1
                  match:
                    path: "{**catch-all}"
                clusters:
                - clusterId: cluster1
                  destinations:
                    destination1:
                      address: {{upstream.Url}}
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient();

        using var response = await httpClient.GetAsync(app.Url("/reverse/path?x=1"));
        RequireEqual(HttpStatusCode.OK, response.StatusCode);
        RequireEqual("upstream:/reverse/path?x=1", await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task BrowserTokenAuthenticationProtectsEndpointsAndAcceptsConfiguredToken()
    {
        var port = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              endpoints:
              - route: /public
                body: Public response
                contentType: text/plain
              - route: /profile
                body: Protected response
                contentType: text/plain
                policy: Kassini
              authentication:
                mode: BrowserToken
                browserToken: test-token
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient(followRedirects: false);

        using var publicResponse = await httpClient.GetAsync(app.Url("/public"));
        RequireEqual(HttpStatusCode.OK, publicResponse.StatusCode);
        RequireEqual("Public response", await publicResponse.Content.ReadAsStringAsync());

        using var anonymousResponse = await httpClient.GetAsync(app.Url("/profile"));
        RequireEqual(HttpStatusCode.Found, anonymousResponse.StatusCode);
        RequireEqual($"http://127.0.0.1:{port}/login?returnUrl=%2Fprofile", anonymousResponse.Headers.Location?.ToString());

        using var loginResponse = await httpClient.GetAsync(app.Url("/login?t=test-token&returnUrl=%2Fprofile"));
        RequireEqual(HttpStatusCode.Found, loginResponse.StatusCode);
        RequireEqual("/profile", loginResponse.Headers.Location?.ToString());
        RequireTrue(loginResponse.Headers.TryGetValues("Set-Cookie", out var cookies), "Expected authentication cookie.");

        using var authenticatedRequest = new HttpRequestMessage(HttpMethod.Get, app.Url("/profile"));
        authenticatedRequest.Headers.TryAddWithoutValidation("Cookie", cookies!.Single());

        using var authenticatedResponse = await httpClient.SendAsync(authenticatedRequest);
        RequireEqual(HttpStatusCode.OK, authenticatedResponse.StatusCode);
        RequireEqual("Protected response", await authenticatedResponse.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task BrowserTokenAuthenticationProtectsEndpointProxies()
    {
        var port = GetFreePort();
        await using var upstream = new UpstreamServer();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              endpoints:
              - route: /proxy/*
                policy: Kassini
                proxy:
                  destination: {{upstream.Url}}
              authentication:
                mode: BrowserToken
                browserToken: proxy-token
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient(followRedirects: false);

        using var anonymousResponse = await httpClient.GetAsync(app.Url("/proxy/downstream?x=1"));
        RequireEqual(HttpStatusCode.Found, anonymousResponse.StatusCode);
        RequireEqual($"http://127.0.0.1:{port}/login?returnUrl=%2Fproxy%2Fdownstream%3Fx%3D1", anonymousResponse.Headers.Location?.ToString());

        using var loginResponse = await httpClient.GetAsync(app.Url("/login?t=proxy-token&returnUrl=%2Fproxy%2Fdownstream%3Fx%3D1"));
        RequireEqual(HttpStatusCode.Found, loginResponse.StatusCode);
        RequireEqual("/proxy/downstream?x=1", loginResponse.Headers.Location?.ToString());
        RequireTrue(loginResponse.Headers.TryGetValues("Set-Cookie", out var cookies), "Expected authentication cookie.");

        using var authenticatedRequest = new HttpRequestMessage(HttpMethod.Get, app.Url("/proxy/downstream?x=1"));
        authenticatedRequest.Headers.TryAddWithoutValidation("Cookie", cookies!.Single());

        using var authenticatedResponse = await httpClient.SendAsync(authenticatedRequest);
        RequireEqual(HttpStatusCode.OK, authenticatedResponse.StatusCode);
        RequireEqual("upstream:/downstream?x=1", await authenticatedResponse.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task GoogleAuthenticationChallengesProtectedEndpointsWithGoogleAuthorizationEndpoint()
    {
        var port = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              endpoints:
              - route: /profile
                body: Protected response
                contentType: text/plain
                policy: Kassini
              authentication:
                mode: Google
                clientId: google-client
                clientSecret: google-secret
                scopes:
                - email
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient(followRedirects: false);

        using var response = await httpClient.GetAsync(app.Url("/profile"));
        RequireEqual(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString();
        RequireStartsWith("https://accounts.google.com/o/oauth2/v2/auth?", location);
        RequireContains("client_id=google-client", location);
        RequireContains(Uri.EscapeDataString($"http://127.0.0.1:{port}/signin-google"), location);
        RequireContains("scope=", location);
    }

    [Test]
    public async Task GitHubAuthenticationChallengesProtectedEndpointsWithGitHubAuthorizationEndpoint()
    {
        var port = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              endpoints:
              - route: /profile
                body: Protected response
                contentType: text/plain
                policy: Kassini
              authentication:
                mode: GitHub
                clientId: github-client
                clientSecret: github-secret
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient(followRedirects: false);

        using var response = await httpClient.GetAsync(app.Url("/profile"));
        RequireEqual(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString();
        RequireStartsWith("https://github.com/login/oauth/authorize?", location);
        RequireContains("client_id=github-client", location);
        RequireContains(Uri.EscapeDataString($"http://127.0.0.1:{port}/signin-github"), location);
        RequireContains("scope=read%3Auser", location);
        RequireContains("user%3Aemail", location);
    }

    [Test]
    public async Task GenericOAuthAuthenticationUsesConfiguredAuthorizationEndpoint()
    {
        var port = GetFreePort();
        var config = $$"""
            servers:
            - bind:
              - address: 127.0.0.1:{{port}}
              endpoints:
              - route: /profile
                body: Protected response
                contentType: text/plain
                policy: Kassini
              authentication:
                mode: OAuth
                authorizationEndpoint: https://provider.example.com/oauth/authorize
                tokenEndpoint: https://provider.example.com/oauth/token
                userInformationEndpoint: https://provider.example.com/oauth/userinfo
                callbackPath: /signin-provider
                clientId: oauth-client
                clientSecret: oauth-secret
                scopes:
                - profile
            """;

        await using var app = await KassiniApplication.StartAsync(port, config);
        using var httpClient = CreateHttpClient(followRedirects: false);

        using var response = await httpClient.GetAsync(app.Url("/profile"));
        RequireEqual(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString();
        RequireStartsWith("https://provider.example.com/oauth/authorize?", location);
        RequireContains("client_id=oauth-client", location);
        RequireContains(Uri.EscapeDataString($"http://127.0.0.1:{port}/signin-provider"), location);
        RequireContains("scope=profile", location);
    }

    private static HttpClient CreateHttpClient(bool followRedirects = true)
    {
        return new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = followRedirects,
            AutomaticDecompression = DecompressionMethods.None
        })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void RequireEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but received '{actual}'.");
        }
    }

    private static void RequireTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireEndsWith(string expectedSuffix, string? actual)
    {
        if (actual == null || !actual.EndsWith(expectedSuffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected value ending with '{expectedSuffix}', but received '{actual}'.");
        }
    }

    private static void RequireStartsWith(string expectedPrefix, string? actual)
    {
        if (actual == null || !actual.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected value starting with '{expectedPrefix}', but received '{actual}'.");
        }
    }

    private static void RequireContains(string expectedValue, string? actual)
    {
        if (actual == null || !actual.Contains(expectedValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected value containing '{expectedValue}', but received '{actual}'.");
        }
    }

    private sealed class KassiniApplication : IAsyncDisposable
    {
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _logs;
        private readonly string _workspaceRoot;
        private readonly int? _singlePort;
        private readonly Task _standardOutputTask;
        private readonly Task _standardErrorTask;

        private KassiniApplication(
            Process process,
            ConcurrentQueue<string> logs,
            string workspaceRoot,
            int? singlePort,
            Task standardOutputTask,
            Task standardErrorTask)
        {
            _process = process;
            _logs = logs;
            _workspaceRoot = workspaceRoot;
            _singlePort = singlePort;
            _standardOutputTask = standardOutputTask;
            _standardErrorTask = standardErrorTask;
        }

        public static async Task<KassiniApplication> StartAsync(int port, string config, IReadOnlyDictionary<string, string>? files = null)
            => await StartAsync([port], config, files);

        public static async Task<KassiniApplication> StartAsync(int[] ports, string config, IReadOnlyDictionary<string, string>? files = null)
        {
            var workspaceRoot = Directory.CreateTempSubdirectory("kassini-functional-").FullName;
            var configPath = Path.Combine(workspaceRoot, "config.yml");
            await File.WriteAllTextAsync(configPath, config);

            if (files != null)
            {
                foreach (var file in files)
                {
                    var filePath = Path.Combine(workspaceRoot, file.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    await File.WriteAllTextAsync(filePath, file.Value);
                }
            }

            var appPath = Path.Combine(AppContext.BaseDirectory, "KassiniApp", "yarp.dll");
            if (!File.Exists(appPath))
            {
                throw new FileNotFoundException("Kassini application output was not copied to the functional test output directory.", appPath);
            }

            var logs = new ConcurrentQueue<string>();
            var processStartInfo = new ProcessStartInfo(ResolveDotNetHostPath())
            {
                WorkingDirectory = workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            processStartInfo.ArgumentList.Add(appPath);
            processStartInfo.ArgumentList.Add(configPath);

            var process = Process.Start(processStartInfo)
                ?? throw new InvalidOperationException("Failed to start Kassini process.");

            var standardOutputTask = DrainOutputAsync(process.StandardOutput, logs);
            var standardErrorTask = DrainOutputAsync(process.StandardError, logs);

            var app = new KassiniApplication(
                process,
                logs,
                workspaceRoot,
                ports.Length == 1 ? ports[0] : null,
                standardOutputTask,
                standardErrorTask);
            try
            {
                foreach (var port in ports)
                {
                    await app.WaitUntilListeningAsync(port);
                }

                return app;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public Uri Url(string path) => Url(_singlePort ?? throw new InvalidOperationException("This application was started with multiple ports."), path);

        public Uri Url(int port, string path) => new(new Uri($"http://127.0.0.1:{port}"), path);

        public string GetWorkspacePath(string relativePath) => Path.Combine(_workspaceRoot, relativePath);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (_process.HasExited)
            {
            }

            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(_standardOutputTask, _standardErrorTask).WaitAsync(TimeSpan.FromSeconds(5));
            _process.Dispose();

            if (Directory.Exists(_workspaceRoot))
            {
                Directory.Delete(_workspaceRoot, recursive: true);
            }
        }

        private async Task WaitUntilListeningAsync(int port)
        {
            var deadline = DateTimeOffset.UtcNow + StartupTimeout;

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException($"Kassini exited before listening on port {port}.{Environment.NewLine}{GetLogs()}");
                }

                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromMilliseconds(200));
                    return;
                }
                catch (SocketException)
                {
                }
                catch (TimeoutException)
                {
                }

                await Task.Delay(100);
            }

            throw new TimeoutException($"Kassini did not start listening on port {port} within {StartupTimeout}.{Environment.NewLine}{GetLogs()}");
        }

        private string GetLogs() => string.Join(Environment.NewLine, _logs);

        private static string ResolveDotNetHostPath()
        {
            var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath))
            {
                return hostPath;
            }

            foreach (var candidate in new[]
            {
                "/usr/local/share/dotnet/dotnet",
                "/usr/local/bin/dotnet",
                "/opt/homebrew/bin/dotnet",
                "/opt/homebrew/share/dotnet/dotnet"
            })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return "dotnet";
        }

        private static async Task DrainOutputAsync(StreamReader reader, ConcurrentQueue<string> logs)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                logs.Enqueue(line);
            }
        }
    }

    private sealed class UpstreamServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly Task _serverTask;

        public UpstreamServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{Port}/";
            _serverTask = RunAsync();
        }

        public int Port { get; }

        public string Url { get; }

        public async ValueTask DisposeAsync()
        {
            await _cancellationTokenSource.CancelAsync();
            _listener.Stop();

            try
            {
                await _serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            _cancellationTokenSource.Dispose();
        }

        private async Task RunAsync()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cancellationTokenSource.Token);
                _ = HandleClientAsync(client);
            }
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            using var _ = client;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync();
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
            {
            }

            var path = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";
            var body = Encoding.UTF8.GetBytes($"upstream:{path}");
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");

            await stream.WriteAsync(headers);
            await stream.WriteAsync(body);
        }
    }
}
