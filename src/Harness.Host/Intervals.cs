using System.Globalization;

namespace Harness.Host;

/// <summary>A configured interval or switch, read one way: a value that does not parse is the default.</summary>
public static class Intervals
{
    public static TimeSpan Read(IConfiguration configuration, string key, TimeSpan fallback) =>
        TimeSpan.TryParse(configuration[key], CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary>As <see cref="Read"/>, and a value of zero or less is the default too.</summary>
    public static TimeSpan ReadPositive(IConfiguration configuration, string key, TimeSpan fallback) =>
        TimeSpan.TryParse(configuration[key], CultureInfo.InvariantCulture, out var value) && value > TimeSpan.Zero ? value : fallback;

    /// <summary>A switch that is on unless the configuration says `false`.</summary>
    public static bool Enabled(IConfiguration configuration, string key) =>
        !bool.TryParse(configuration[key], out var value) || value;
}
