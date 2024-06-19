internal sealed class StatusCodeFilter : IEndpointFilter
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
