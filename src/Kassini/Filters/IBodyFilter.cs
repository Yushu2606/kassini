namespace Kassini.Filters;

public interface IBodyFilter
{
    public Task<string> FilterAsync(string body, IDictionary<string, object?> values);
}
