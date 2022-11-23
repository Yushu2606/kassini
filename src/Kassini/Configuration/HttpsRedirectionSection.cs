namespace Kassini.Configuration;

public class HttpsRedirectionSection
{
    public bool Enabled { get; set; } = false;
    public int? Port { get; set; } = null;
}
