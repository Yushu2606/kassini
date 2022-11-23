using Kassini.Configuration;

namespace Kassini.Filters;

internal sealed class MarkdownFilter : IBodyFilter
{
    public static MarkdownFilter Instance = new();

    public Task<string> FilterAsync(string body, IDictionary<string, object?> values)
    {
        return Task.FromResult(Markdig.Markdown.ToHtml(body));
    }

    public static void Register(Dictionary<string, Func<FilterSection, IBodyFilter>> factory)
    {
        factory["markdown"] = settings =>
        {
            return Instance;
        };
    }
}
