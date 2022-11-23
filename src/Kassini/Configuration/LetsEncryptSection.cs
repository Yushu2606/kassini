namespace Kassini.Configuration;

public class LetsEncryptSection
{
    public string? Email { get; set; } = null;
    public string Path { get; set; } = "./certificates";
    public string[] Domains { get; set; } = Array.Empty<string>();
}
