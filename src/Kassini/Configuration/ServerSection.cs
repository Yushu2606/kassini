namespace Kassini.Configuration;

public class ServerSection
{
    public string? Name { get; set; } = null;
    public BindSection[] Bind { get; set; } = [];
    public List<EndpointSection> Endpoints { get; set; } = [];
    public ResponseCompressionSection? ResponseCompression { get; set; } = null;
    public HttpsRedirectionSection? HttpsRedirection { get; set; } = null;
    public RedirectSection[] Redirect { get; set; } = [];
    public RewriteSection[] Rewrite { get; set; } = [];
}
