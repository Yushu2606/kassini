namespace Kassini.Configuration;

public class ServerSection
{
    public string? Name { get; set; } = null;
    public BindSection[] Bind { get; set; } = Array.Empty<BindSection>();
    public List<EndpointSection> Endpoints { get; set; } = new();
    public ResponseCompressionSection? ResponseCompression { get; set; } = null;
    public HttpsRedirectionSection? HttpsRedirection { get; set; } = null;
    public RedirectSection[] Redirect { get; set; } = Array.Empty<RedirectSection>();
    public RewriteSection[] Rewrite { get; set; } = Array.Empty<RewriteSection>();
}
