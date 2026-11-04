using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>
/// WHO MAY WRITE WHICH OF THE TEAM PICTURE'S COLLECTIONS, and the shapes the Host holds them to.
/// Every write to the reserved site <c>team-picture</c> - from <c>/api</c>, the <c>site</c> tool, a
/// plugin's <c>site.put</c>, the page, the platform - passes through <see cref="WriteRefusal"/> in
/// <see cref="SiteService"/>, so there is one answer whichever door it came by.
///
/// <list type="bullet">
/// <item>The platform's collections (<see cref="PlatformCollections"/>) are written by the platform
/// alone, recomputed from the team's own figures; nobody else, a person included.</item>
/// <item><c>tp-picture</c>: the team's Manager, the Concierge or a person through <c>/api</c>.</item>
/// <item><c>tp-settings</c>: the Concierge or a person through <c>/api</c>.</item>
/// <item><c>tp-ops</c> and <c>tp-extra-&lt;slug&gt;</c>: any member or plugin of the team, the
/// Concierge or a person through <c>/api</c>.</item>
/// <item>The page itself writes nothing on this site, and no other collection name is kept.</item>
/// </list>
/// Another team's site already answers as missing before this is asked (<see cref="SiteActor.BoundTeam"/>).
/// </summary>
public static partial class TeamPictureRules
{
    public const string Team = "tp-team";
    public const string Items = "tp-items";
    public const string Now = "tp-now";
    public const string Runs = "tp-runs";
    public const string Spend = "tp-spend";
    public const string Waiting = "tp-waiting";
    public const string History = "tp-history";
    public const string Picture = "tp-picture";
    public const string Settings = "tp-settings";
    public const string Ops = "tp-ops";
    public const string ExtraPrefix = "tp-extra-";

    /// <summary>The collections the platform keeps, from its own figures.</summary>
    public static readonly IReadOnlyList<string> PlatformCollections = [Team, Items, Now, Runs, Spend, Waiting, History];

    /// <summary>The most runs and latest items a <c>tp-ops</c> document may carry.</summary>
    public const int MaxOpsRuns = 400;

    public const int MaxOpsItems = 50;

    public sealed record Refusal(string Sentence, int Status);

    public static bool IsPlatformCollection(string collection) => PlatformCollections.Contains(collection);

    /// <summary>
    /// Why <paramref name="actor"/> may not write (or, with no <paramref name="json"/>, delete)
    /// <paramref name="collection"/> of the Team picture, or null when it may.
    /// </summary>
    public static Refusal? WriteRefusal(string collection, SiteActor actor, string? json)
    {
        if (actor.Kind == SiteActorKind.Page) return Forbidden("The Team picture page writes no data.");

        var person = actor.Kind is SiteActorKind.Person or SiteActorKind.Concierge;
        var team = actor.Kind is SiteActorKind.Member or SiteActorKind.Plugin;

        if (IsPlatformCollection(collection))
        {
            return actor.Kind == SiteActorKind.Platform
                ? null
                : Forbidden($"`{collection}` is kept by the platform from the team's own figures; nobody writes it.");
        }

        switch (collection)
        {
            case Picture:
                if (!(person || actor.IsManager || actor.Kind == SiteActorKind.Platform))
                {
                    return Forbidden($"Only the team's Manager or the Concierge draws the Team picture (`{Picture}`).");
                }

                return json is null ? null : PictureShape(json);

            case Settings:
                if (!person) return Forbidden($"Only the Concierge changes the Team picture's settings (`{Settings}`).");
                return json is null ? null : ObjectShape(json, Settings);

            case Ops:
                if (!(person || team)) return Forbidden($"`{Ops}` is written by the team's own members and plugins.");
                return json is null ? null : OpsShape(json);

            default:
                if (collection.StartsWith(ExtraPrefix, StringComparison.Ordinal) && collection.Length > ExtraPrefix.Length)
                {
                    return person || team ? null : Forbidden($"`{collection}` is written by the team's own members and plugins.");
                }

                return Forbidden(
                    $"The Team picture keeps only its own collections ({string.Join(", ", PlatformCollections)}, "
                    + $"{Picture}, {Settings}, {Ops} and {ExtraPrefix}<name>); \"{collection}\" is not one of them.");
        }
    }

    private static Refusal Forbidden(string sentence) => new(sentence, 403);

    private static Refusal Bad(string sentence) => new(sentence, 400);

    private static Refusal? ObjectShape(string json, string collection)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? null
            : Bad($"A `{collection}` document is a JSON object.");
    }

    private static Refusal? PictureShape(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object) return Bad($"A `{Picture}` document is a JSON object.");

        if (root.TryGetProperty("kind", out var kind)
            && !(kind.ValueKind == JsonValueKind.String && kind.GetString() is "build" or "operational"))
        {
            return Bad($"A `{Picture}` document's `kind` is \"build\" or \"operational\".");
        }

        return null;
    }

    /// <summary>The documented operational shape (docs/sites.md): only the fields it names are
    /// checked, and any other field is kept as written.</summary>
    private static Refusal? OpsShape(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object) return Bad($"A `{Ops}` document is a JSON object.");

        if (!root.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.String
            || !DatePattern().IsMatch(date.GetString()!))
        {
            return Bad($"A `{Ops}` document needs `date`, the day its figures are for, as YYYY-MM-DD.");
        }

        if (!root.TryGetProperty("counts", out var counts) || counts.ValueKind != JsonValueKind.Object)
        {
            return Bad($"A `{Ops}` document needs `counts`, an object of each kind's count for the day.");
        }

        foreach (var count in counts.EnumerateObject())
        {
            if (count.Value.ValueKind != JsonValueKind.Number || !count.Value.TryGetInt64(out var n) || n < 0)
            {
                return Bad($"`counts.{count.Name}` in `{Ops}` is a whole number of 0 or more.");
            }
        }

        if (root.TryGetProperty("lastMessage", out var last) && last.ValueKind != JsonValueKind.Null)
        {
            if (last.ValueKind != JsonValueKind.Object
                || !IsString(last, "to") || !IsString(last, "sentAt") || !IsString(last, "text"))
            {
                return Bad($"`lastMessage` in `{Ops}` is null or {{to, sentAt, text}}, each a string.");
            }
        }

        if (root.TryGetProperty("runs", out var runs))
        {
            if (runs.ValueKind != JsonValueKind.Array) return Bad($"`runs` in `{Ops}` is an array.");
            if (runs.GetArrayLength() > MaxOpsRuns) return Bad($"`runs` in `{Ops}` holds at most {MaxOpsRuns} runs; keep the newest.");

            foreach (var run in runs.EnumerateArray())
            {
                if (run.ValueKind != JsonValueKind.Object || !IsString(run, "at")
                    || !(run.TryGetProperty("kind", out var k) && k.ValueKind is JsonValueKind.String or JsonValueKind.Null))
                {
                    return Bad($"Each of `runs` in `{Ops}` is {{at, kind}}: `at` a string, `kind` a kind name or null for nothing new.");
                }
            }
        }

        if (root.TryGetProperty("items", out var items))
        {
            if (items.ValueKind != JsonValueKind.Array) return Bad($"`items` in `{Ops}` is an array.");
            if (items.GetArrayLength() > MaxOpsItems) return Bad($"`items` in `{Ops}` holds at most {MaxOpsItems} items; keep the newest.");

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !IsString(item, "at") || !IsString(item, "kind") || !IsString(item, "title"))
                {
                    return Bad($"Each of `items` in `{Ops}` is {{at, kind, title}}, each a string.");
                }
            }
        }

        return null;
    }

    private static bool IsString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();
}
