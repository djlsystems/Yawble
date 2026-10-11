using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// HOW THE APP LOOKS TO ONE PERSON: the theme they chose (Match system, a preset, or one of their own),
/// the text size, the font, the line spacing, and their own themes. Kept on the person
/// (<c>users.appearance</c>, auth-023) so it follows them to every browser; someone who never chose
/// has none and gets the instance default, <see cref="TenantSettings.ThemeDefaultName"/>.
///
/// THE HOST CHECKS, THE WEB DRAWS. The web owns what each preset and colour looks like; the Host
/// owns which names exist and what is in bounds, so a stored record is always one the web can apply.
/// The web's own lists are held to these by <c>AppearanceTests.The_webs_presets_and_fonts_are_the_hosts</c>.
///
/// It is the person's own screen, not an instance setting: a change writes no tenant row.
/// </summary>
public static partial class Appearance
{
    /// <summary>The built-in themes, in the order the Themes dialog lists them.</summary>
    public static readonly IReadOnlyList<string> Presets =
        ["light", "dark", "high-contrast-light", "high-contrast-dark", "soft-dark"];

    /// <summary>The fonts the app bundles; it loads nothing from elsewhere.</summary>
    public static readonly IReadOnlyList<string> Fonts = ["plex", "atkinson", "system"];

    /// <summary>The colours a person's own theme sets; everything else follows from them.</summary>
    public static readonly IReadOnlyList<string> Colours =
        ["background", "panels", "text", "accent", "running", "waiting", "blocked", "failed"];

    public static readonly IReadOnlyList<string> LineSpacings = ["normal", "relaxed"];

    public const string MatchSystem = "auto";

    public const string CustomPrefix = "custom:";

    public const int MinTextSize = 90;

    public const int MaxTextSize = 160;

    public const int TextSizeStep = 10;

    public const int MaxThemes = 20;

    public const int MaxNameLength = 40;

    /// <summary>The instance default's choices: Match system or a preset, never a person's own.</summary>
    public static IReadOnlyList<string> DefaultChoices => [MatchSystem, .. Presets];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex ThemeIdPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColourPattern();

    /// <summary>
    /// Why this record cannot be stored, as one sentence naming the field in backticks, or null when
    /// every field is in bounds. Every field is optional: one left out is the default.
    /// </summary>
    public static string? Refusal(JsonObject record)
    {
        var themeIds = new List<string>();

        if (record["themes"] is { } themesNode)
        {
            if (themesNode is not JsonArray themes)
            {
                return "`themes` must be a list.";
            }

            if (themes.Count > MaxThemes)
            {
                return $"`themes` holds {themes.Count} themes; at most {MaxThemes} are kept.";
            }

            for (var i = 0; i < themes.Count; i++)
            {
                if (ThemeRefusal(themes[i], $"themes[{i}]", themeIds) is { } refusal) return refusal;
            }
        }

        if (record["selected"] is { } selectedNode)
        {
            var selected = Text(selectedNode);
            var known = selected == MatchSystem
                || (selected is not null && Presets.Contains(selected))
                || (selected?.StartsWith(CustomPrefix, StringComparison.Ordinal) == true
                    && themeIds.Contains(selected[CustomPrefix.Length..]));

            if (!known)
            {
                return $"`selected` must be {MatchSystem}, one of {string.Join(", ", Presets)}, or "
                    + $"{CustomPrefix}<id> naming one of your own themes.";
            }
        }

        if (record["textSize"] is { } sizeNode)
        {
            if (sizeNode is not JsonValue value || !value.TryGetValue<int>(out var size)
                || size < MinTextSize || size > MaxTextSize || size % TextSizeStep != 0)
            {
                return $"`textSize` must be a percentage from {MinTextSize} to {MaxTextSize}, in steps of {TextSizeStep}.";
            }
        }

        if (record["font"] is { } fontNode && !Fonts.Contains(Text(fontNode) ?? ""))
        {
            return $"`font` must be one of {string.Join(", ", Fonts)}.";
        }

        if (record["lineSpacing"] is { } spacingNode && !LineSpacings.Contains(Text(spacingNode) ?? ""))
        {
            return $"`lineSpacing` must be one of {string.Join(", ", LineSpacings)}.";
        }

        return null;
    }

    private static string? ThemeRefusal(JsonNode? node, string at, List<string> ids)
    {
        if (node is not JsonObject theme)
        {
            return $"`{at}` must be a theme: id, name, basedOn and colours.";
        }

        var id = Text(theme["id"]);
        if (id is null || !ThemeIdPattern().IsMatch(id))
        {
            return $"`{at}.id` must be lowercase letters, digits and hyphens, at most 40.";
        }

        if (ids.Contains(id))
        {
            return $"`{at}.id` '{id}' is used by another of your themes.";
        }

        ids.Add(id);

        var name = Text(theme["name"])?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            return $"`{at}.name` must be one line of 1 to {MaxNameLength} characters.";
        }

        if (!Presets.Contains(Text(theme["basedOn"]) ?? ""))
        {
            return $"`{at}.basedOn` must be one of {string.Join(", ", Presets)}.";
        }

        if (theme["colours"] is { } coloursNode)
        {
            if (coloursNode is not JsonObject colours)
            {
                return $"`{at}.colours` must name colours: {string.Join(", ", Colours)}.";
            }

            foreach (var (key, value) in colours)
            {
                if (!Colours.Contains(key))
                {
                    return $"`{at}.colours.{key}` is not a colour a theme sets; those are {string.Join(", ", Colours)}.";
                }

                if (Text(value) is not { } colour || !ColourPattern().IsMatch(colour))
                {
                    return $"`{at}.colours.{key}` must be a colour written #rrggbb.";
                }
            }
        }

        return null;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/me/appearance", async (HttpContext context, IUserStore users, TenantSettings settings, CancellationToken ct) =>
            {
                if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.User } person) return NotAPerson();

                var stored = await users.AppearanceForAsync(person.Id, ct);

                return Results.Ok(new
                {
                    appearance = stored is null ? null : JsonNode.Parse(stored),
                    instanceDefault = settings.ThemeDefault,
                });
            })
            .HumansOnly()
            .WithTags("Appearance")
            .WithSummary("How the app looks to the signed-in person")
            .WithDescription(
                "`appearance` is null until the person chooses; the web then shows `instanceDefault`, the "
                + "`theme.default` setting (auto, or a preset). Otherwise it is what they saved: `selected` "
                + "(auto, a preset, or custom:<id> naming one of `themes`), `textSize` (a percentage), `font`, "
                + "`lineSpacing` and `themes` (each with id, name, basedOn and the colours it sets).");

        app.MapPut("/api/me/appearance", async (JsonObject body, HttpContext context, IUserStore users, CancellationToken ct) =>
            {
                if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.User } person) return NotAPerson();

                if (Refusal(body) is { } refusal) return Results.BadRequest(new { error = refusal });

                await users.SetAppearanceAsync(person.Id, body.ToJsonString(), ct);

                return Results.NoContent();
            })
            .HumansOnly()
            .WithTags("Appearance")
            .WithSummary("Save how the app looks to the signed-in person")
            .WithDescription(
                "The whole record, replacing what was stored. Each field is optional and in bounds or refused "
                + "with 400 naming it: `selected` auto, a preset or custom:<id> of one of `themes`; `textSize` "
                + "90 to 160 in steps of 10; `font` plex, atkinson or system; `lineSpacing` normal or relaxed; at "
                + "most 20 `themes`, each with a unique lowercase `id`, a one-line `name`, the preset it is "
                + "`basedOn` and `colours` (background, panels, text, accent, running, waiting, blocked, failed) "
                + "as #rrggbb. The person's own screen: no tenant row is written.");
    }

    private static IResult NotAPerson() =>
        Results.Json(new { error = "Only a person has an appearance." }, statusCode: StatusCodes.Status403Forbidden);
}
