internal sealed class HeadersFilter : IEndpointFilter
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
