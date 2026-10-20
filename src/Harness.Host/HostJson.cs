using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Host;

/// <summary>
/// THE JSON OPTIONS A ROUTE WRITES A NODE WITH, made once: System.Text.Json caches its metadata per
/// options instance, so an options object made per request serialised every type cold. Read-only, so
/// no site can change what the others write with.
/// </summary>
public static class HostJson
{
    /// <summary>The web defaults: camel case, case-insensitive reads.</summary>
    public static readonly JsonSerializerOptions Web = Frozen(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>The web defaults with enums as their names, as every answer the SPA reads spells them.</summary>
    public static readonly JsonSerializerOptions WebEnums = Frozen(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    });

    private static JsonSerializerOptions Frozen(JsonSerializerOptions options)
    {
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
