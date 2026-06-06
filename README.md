# Kassini

Kassini is a configurable .NET reverse proxy and lightweight endpoint host. It reads a YAML, JSON or TOML configuration file and starts one or more HTTP servers with static responses, files, redirects, rewrites, proxy routes, compression, caching, rate limiting, certificates, and authentication.

## Run Kassini

Pass the configuration file path as the first argument. If no argument is provided, Kassini loads `kassini.config.yml` or `kassini.config.json` from the current directory when either exists; otherwise it starts with an empty configuration and exits successfully.

```console
dotnet run --project src/Kassini -- src/Kassini/simple.yml
```

Published single-file builds use the `kassini` executable name:

```console
./.artifacts/linux-x64/kassini ./config.yml
.\.artifacts\win-x64\kassini.exe .\config.yml
```

Creating a GitHub Release with a tag matching `v*.*.*` publishes single-file release assets for Windows, Linux, and macOS on x64 and arm64. Windows assets are `.zip` files; Linux and macOS assets are `.tar.gz` files that preserve the executable bit.

Build and run the Docker image locally:

```console
# Builds the `kassini:latest` Docker image locally
./docker-build.ps1

# Starts a new container named `myproxy` with the local configuration file `.\src\Kassini\simple.yml`
.\docker-run.ps1 -name myproxy -config .\src\Kassini\simple.yml

# Stops and removes the container named `myproxy`
.\docker-stop.ps1 -name myproxy
```

Use Kassini from a .NET Aspire AppHost:

```csharp
var kassini = new ContainerResource("kassini");

builder.AddResource(kassini)
    .WithImage("kassini", "latest")
    .WithBindMount("D:\\kassini\\src\\Kassini\\simple.yml", "/etc/kassini/config.yml")
    .WithHttpEndpoint(targetPort: 8084, name: "http")
    .WithOtlpExporter();
```

## Configuration format

Kassini accepts `.yml`, `.yaml`, `.json` and `.toml` configuration files. YAML examples use camel-case property names; JSON and TOML uses the same property names.

The top-level configuration contains:

```yaml
servers: []
cachePolicies: []
rateLimitPolicies: []
certificates: []
letsEncrypt: null
```

## Servers, ports, and protocols

Each `servers` entry creates a web server. Use `bind` to configure listener addresses, optional TLS certificates, and HTTP protocols.

```yaml
servers:
- name: public
  bind:
  - address: '*:8084'
    protocols:
    - http1
  - address: 'localhost:8443'
    certificate: dev
    protocols:
    - http1
    - http2
  endpoints:
  - route: /
    body: Hello from Kassini
```

`address` supports `*:port`, `host:port`, a bare port, or a bare address. If `protocols` is omitted, Kassini enables HTTP/1.1 and HTTP/2. `certificate` can be `dev`, `letsencrypt`, or the name of an entry in `certificates`.

## Static response endpoints

Use `body` for inline responses and `contentType` to set the response content type.

```yaml
servers:
- bind:
  - address: '*:8084'
  endpoints:
  - route: /
    body: <h1>Hello World</h1>
    contentType: text/html
```

## File response endpoints

Use `file.path` to return a single physical file.

```yaml
servers:
- bind:
  - address: '*:8084'
  endpoints:
  - route: /favicon.ico
    contentType: image/x-icon
    file:
      path: wwwroot/favicon.ico
```

## Static file directories

Use `files.path` to serve a directory at a route prefix. `mimeTypes` can override or add extension mappings.

```yaml
servers:
- bind:
  - address: '*:8084'
  endpoints:
  - route: /static
    files:
      path: wwwroot
      mimeTypes:
        .cast: application/x-cast
```

## Methods, status codes, and headers

Use `methods` to map an endpoint to one or more HTTP methods. Use `status` and `headers` to customize the response metadata.

```yaml
servers:
- bind:
  - address: '*:8084'
  endpoints:
  - route: /created
    methods: POST
    status: 201
    headers:
      X-Powered-By: Kassini
    body: Created
```

`methods` defaults to `GET` and accepts comma- or space-separated values, such as `GET,POST`.

## Redirect endpoints

Use `redirect` on an endpoint to send callers to another URL.

```yaml
servers:
- bind:
  - address: '*:8084'
  endpoints:
  - route: /docs
    redirect: /docs/index.html
```

## Server redirects and rewrites

Use server-level `redirect` for regular-expression redirects and `rewrite` for regular-expression rewrites before endpoint routing.

```yaml
servers:
- bind:
  - address: '*:8084'
  redirect:
  - from: old/(.*)
    to: new/$1
  rewrite:
  - from: ^api/v1/(.*)
    to: api/$1
    skipRemainingRules: true
  endpoints:
  - route: /new/{name}
    body: Redirected
  - route: /api/{name}
    body: Rewritten
```

## Proxy endpoints

Use `proxy.destination` to forward an endpoint to another origin. By default, Kassini removes the matched route prefix before forwarding; set `removePrefix: false` to keep it.

```yaml
servers:
- bind:
  - address: '*:8084'
  endpoints:
  - route: /microsoft
    proxy:
      destination: https://www.microsoft.com/en-us
  - route: /api/*
    proxy:
      destination: https://api.example.com/
      removePrefix: false
```

Routes ending in `/*` match a route group. A route value of `*` proxies every path.

## Advanced reverse proxy configuration

Use `reverseProxy` when you need full route and cluster configuration rather than one `proxy.destination` per endpoint.

```yaml
servers:
- bind:
  - address: '*:8084'
  reverseProxy:
    routes:
    - routeId: app
      clusterId: app
      match:
        path: "{**catch-all}"
    clusters:
    - clusterId: app
      destinations:
        app/server1:
          address: https://example.com/
```

## Response compression

Enable response compression per server.

```yaml
servers:
- bind:
  - address: '*:8084'
  responseCompression:
    enabled: true
  endpoints:
  - route: /
    body: Compressible response
```

## Output caching

Use `cache` on an endpoint or route group. Inline cache settings can define `duration`; shared cache policies can be defined in `cachePolicies` and referenced by name.

```yaml
servers:
- bind:
  - address: '*:8084'
  endpoints:
  - route: /cached
    body: This response is cached for 5 seconds
    cache:
      duration: 5s
  - route: /long-cache
    body: This response uses a shared cache policy
    cache:
      policy: long

cachePolicies:
- name: long
  duration: 1m
```

Durations support `ms`, `s`, `m`, `h`, and `d`. A number without a suffix is treated as seconds.

## Rate limiting

Use `rateLimit` on an endpoint or route group. Inline settings can define `duration`, `permit`, `queue`, and `partition`; shared rate-limit policies can be defined in `rateLimitPolicies` and referenced by name.

```yaml
servers:
- bind:
  - address: '*:8084'
  endpoints:
  - route: /limited
    body: Slow down
    rateLimit:
      duration: 10s
      permit: 3
      queue: 1
      partition: IpAddress
  - route: /shared-limit
    body: Shared policy
    rateLimit:
      policy: signed-in-users

rateLimitPolicies:
- name: signed-in-users
  duration: 1m
  permit: 30
  partition: User
```

`partition` can be `None`, `IpAddress`, `ConnectionId`, or `User`.

## HTTPS redirection

Use `httpsRedirection` to redirect HTTP requests to HTTPS.

```yaml
servers:
- bind:
  - address: '*:80'
  - address: '*:443'
    certificate: dev
  httpsRedirection:
    enabled: true
    port: 443
  endpoints:
  - route: /
    body: Secure endpoint
```

## Certificates

Use a named certificate from the top-level `certificates` collection by referencing its `name` from `bind.certificate`.

```yaml
servers:
- bind:
  - address: '*:443'
    certificate: site
  endpoints:
  - route: /
    body: HTTPS with a certificate file

certificates:
- name: site
  path: certs/site.pfx
  password: cert-password
```

## Let's Encrypt

Use `certificate: letsencrypt` and configure `letsEncrypt` with the certificate storage path, account email, and domain names.

```yaml
servers:
- bind:
  - address: '*:443'
    certificate: letsencrypt
  endpoints:
  - route: /
    body: HTTPS with Let's Encrypt

letsEncrypt:
  path: ./.certificates
  email: admin@example.com
  domains:
  - example.com
```

## Browser token authentication

Use `authentication.mode: BrowserToken` to protect routes with a local token. Endpoints that set `policy: Kassini` require a signed-in browser-token user.

```yaml
servers:
- bind:
  - address: '*:8084'
  authentication:
    mode: BrowserToken
    browserToken: local-demo-token
  endpoints:
  - route: /
    body: Public
  - route: /profile
    body: Protected
    policy: Kassini
```

Users can sign in with the generated `/login` form or with `/login?t=local-demo-token`.

## External authentication

Kassini supports `Google`, `GitHub`, `OpenIdConnect`, and generic `OAuth` modes. Authenticated endpoints use `policy: Kassini`; additional named policies can require roles or claims.

```yaml
servers:
- bind:
  - address: '*:8084'
  authentication:
    mode: GitHub
    clientId: your-github-client-id
    clientSecret: your-github-client-secret
    scopes:
    - read:user
    policies:
      admins:
        claims:
          urn:github:name: Octocat
  endpoints:
  - route: /account
    body: Signed in
    policy: Kassini
  - route: /admin
    body: Admin
    policy: admins
```

Google authentication uses `mode: Google` with `clientId`, `clientSecret`, optional `callbackPath`, and optional `scopes`.

```yaml
authentication:
  mode: Google
  clientId: your-google-client-id
  clientSecret: your-google-client-secret
  scopes:
  - email
```

OpenID Connect uses `mode: OpenIdConnect` and requires either `authority` or `metadataAddress`.

```yaml
authentication:
  mode: OpenIdConnect
  authority: https://login.example.com
  clientId: your-oidc-client-id
  clientSecret: your-oidc-client-secret
  callbackPath: /signin-oidc
  saveTokens: true
  claimType: email
  claimValue: admin@example.com
```

Generic OAuth uses explicit authorization, token, and user-information endpoints. `claimMappings` maps local claim types to JSON fields returned by the user-information endpoint.

```yaml
authentication:
  mode: OAuth
  authorizationEndpoint: https://provider.example.com/oauth/authorize
  tokenEndpoint: https://provider.example.com/oauth/token
  userInformationEndpoint: https://provider.example.com/oauth/userinfo
  clientId: your-oauth-client-id
  clientSecret: your-oauth-client-secret
  claimMappings:
    http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier: id
    http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name: name
```

Set `mode: Unsecured` when you want authorization policies to allow every request.

## Route groups

Routes ending with `/*` create a group. Settings such as caching, rate limits, and authorization can be applied to the group and inherited by matching endpoints.

```yaml
servers:
- bind:
  - address: '*:8084'
  authentication:
    mode: BrowserToken
    browserToken: local-demo-token
  endpoints:
  - route: /api/*
    policy: Kassini
    rateLimit:
      duration: 10s
      permit: 10
  - route: /api/hello
    body: Protected API response
```

## Complete sample

See `src/Kassini/config.yml` for a larger sample that combines multiple servers and features. See `src/Kassini/auth.yml` for authentication examples.
