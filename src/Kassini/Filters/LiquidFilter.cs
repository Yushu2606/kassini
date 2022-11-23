using Fluid;
using Kassini.Configuration;

namespace Kassini.Filters;

internal sealed class LiquidFilter : IBodyFilter
{
    private static readonly FluidParser _parser = new FluidParser();

    public static LiquidFilter Instance = new();

    public Task<string> FilterAsync(string body, IDictionary<string, object?> values)
    {
        var templateContext = new TemplateContext();

        foreach (var item in values) 
        { 
            templateContext.SetValue(item.Key, item.Value);
        }
        
        return _parser.Parse(body).RenderAsync(templateContext).AsTask();
    }

    public static void Register(Dictionary<string, Func<FilterSection, IBodyFilter>> factory)
    {
        factory["liquid"] = settings =>
        {
            return Instance;
        };
    }
}
