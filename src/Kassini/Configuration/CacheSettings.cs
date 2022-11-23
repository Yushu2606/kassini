namespace Kassini.Configuration;

public class CacheSettings
{
    public string? Duration { get; set; } = null;

    public TimeSpan GetDuration()
    {
        if (Duration == null)
        {
            return TimeSpan.Zero;
        }

        if (Duration.EndsWith("s"))
        {
            if (int.TryParse(Duration[..^1], out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }
        else if (Duration.EndsWith("ms"))
        {
            if (int.TryParse(Duration[..^2], out var milliseconds))
            {
                return TimeSpan.FromMilliseconds(milliseconds);
            }
        }
        else if (Duration.EndsWith("m"))
        {
            if (int.TryParse(Duration[..^1], out var minutes))
            {
                return TimeSpan.FromMinutes(minutes);
            }
        }
        else if (Duration.EndsWith("h"))
        {
            if (int.TryParse(Duration[..^1], out var hours))
            {
                return TimeSpan.FromHours(hours);
            }
        }
        else if (Duration.EndsWith("d"))
        {
            if (int.TryParse(Duration[..^1], out var days))
            {
                return TimeSpan.FromDays(days);
            }
        }
        else if (int.TryParse(Duration, out var nounits))
        {
            return TimeSpan.FromSeconds(nounits);
        }

        return TimeSpan.Zero;
    }
}
