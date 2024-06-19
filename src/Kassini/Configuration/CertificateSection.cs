namespace Kassini.Configuration;

public class CertificateSection
{
    public string? Name { get; set; } = null;
    public string? Path { get; set; } = null;
    public string? Password { get; set; } = null;
    public string[] Domains { get; set; } = [];
}
