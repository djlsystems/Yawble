using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yawble.Core.Teams;

/// <summary>A country of the member-name list's Generate: its code ("en-US") and its label ("United States (US English)").</summary>
public sealed record NameCountry(string Code, string Label)
{
    /// <summary>The country alone, without its language: "United States".</summary>
    public string DisplayName() => Label.IndexOf(" (", StringComparison.Ordinal) is var i and > 0 ? Label[..i] : Label;
}

/// <summary>A line of the member-name list that was not saved, and why.</summary>
public sealed record RejectedName(string Line, string Reason);

/// <summary>
/// The person's member-name list (Settings > Teams): the first names new members are named from. A list name is one
/// word of ASCII letters (a hyphen inside allowed), 2 to 20 characters, so a member's id keeps the whole name. A
/// member's name is its job word, a space, then a first name ("Developer Priya"); a name with no space is the job
/// word alone, and the app adds a first name picked from the list (<see cref="Pick"/>).
/// </summary>
public static partial class MemberNameList
{
    public const int MinLength = 2;
    public const int MaxLength = 20;
    public const int PickTries = 10;
    public const string DefaultCountry = "en-US";

    [GeneratedRegex("^[A-Za-z]+(?:-[A-Za-z]+)*$")]
    private static partial Regex NameShape();

    /// <summary>The countries Generate offers, each with its locale label.</summary>
    public static readonly IReadOnlyList<NameCountry> Countries =
    [
        new("en-US", "United States (US English)"),
        new("en-GB", "United Kingdom (British English)"),
        new("en-CA", "Canada (English)"),
        new("fr-CA", "Canada (French)"),
        new("en-AU", "Australia (English)"),
        new("en-NZ", "New Zealand (English)"),
        new("en-IE", "Ireland (English)"),
        new("en-IN", "India (English)"),
        new("hi-IN", "India (Hindi)"),
        new("en-ZA", "South Africa (English)"),
        new("en-NG", "Nigeria (English)"),
        new("en-GH", "Ghana (English)"),
        new("sw-KE", "Kenya (Swahili)"),
        new("ar-EG", "Egypt (Arabic)"),
        new("ar-SA", "Saudi Arabia (Arabic)"),
        new("tr-TR", "Turkey (Turkish)"),
        new("he-IL", "Israel (Hebrew)"),
        new("fa-IR", "Iran (Persian)"),
        new("de-DE", "Germany (German)"),
        new("de-AT", "Austria (German)"),
        new("de-CH", "Switzerland (German)"),
        new("fr-FR", "France (French)"),
        new("nl-NL", "Netherlands (Dutch)"),
        new("nl-BE", "Belgium (Dutch)"),
        new("es-ES", "Spain (Spanish)"),
        new("pt-PT", "Portugal (Portuguese)"),
        new("it-IT", "Italy (Italian)"),
        new("el-GR", "Greece (Greek)"),
        new("sv-SE", "Sweden (Swedish)"),
        new("nb-NO", "Norway (Norwegian)"),
        new("da-DK", "Denmark (Danish)"),
        new("fi-FI", "Finland (Finnish)"),
        new("pl-PL", "Poland (Polish)"),
        new("cs-CZ", "Czechia (Czech)"),
        new("hu-HU", "Hungary (Hungarian)"),
        new("ro-RO", "Romania (Romanian)"),
        new("uk-UA", "Ukraine (Ukrainian)"),
        new("ru-RU", "Russia (Russian)"),
        new("es-MX", "Mexico (Spanish)"),
        new("es-AR", "Argentina (Spanish)"),
        new("es-CO", "Colombia (Spanish)"),
        new("pt-BR", "Brazil (Portuguese)"),
        new("zh-CN", "China (Mandarin Chinese)"),
        new("ja-JP", "Japan (Japanese)"),
        new("ko-KR", "South Korea (Korean)"),
        new("vi-VN", "Vietnam (Vietnamese)"),
        new("th-TH", "Thailand (Thai)"),
        new("id-ID", "Indonesia (Indonesian)"),
        new("fil-PH", "Philippines (Filipino)"),
    ];

    /// <summary>The country with this code (case-insensitive), or null.</summary>
    public static NameCountry? Country(string? code) =>
        Countries.FirstOrDefault(c => string.Equals(c.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Why a name cannot be on the list, or null when it can.</summary>
    public static string? Problem(string name)
    {
        if (name.Length < MinLength) return $"a name has at least {MinLength} letters";
        if (name.Length > MaxLength) return $"a name has at most {MaxLength} characters";
        if (name.Any(char.IsWhiteSpace)) return "one name per line, a single word";
        if (!NameShape().IsMatch(name)) return "only the letters A-Z (a hyphen inside a name is allowed)";
        return null;
    }

    /// <summary>
    /// The list from the text the person typed or Generate gave: surrounding spaces and blank lines dropped, a
    /// case-insensitive repeat dropped (the first kept), and lines that are not one list name reported and left out.
    /// </summary>
    public static (IReadOnlyList<string> Names, IReadOnlyList<RejectedName> Rejected) Clean(IEnumerable<string?> lines)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejected = new List<RejectedName>();
        foreach (var raw in lines)
        {
            var line = (raw ?? "").Trim();
            if (line.Length == 0) continue;
            if (Problem(line) is { } why)
            {
                rejected.Add(new RejectedName(line, why));
                continue;
            }
            if (seen.Add(line)) names.Add(line);
        }
        return (names, rejected);
    }

    /// <summary><see cref="Clean(IEnumerable{string?})"/> of a textarea's text, one name per line.</summary>
    public static (IReadOnlyList<string> Names, IReadOnlyList<RejectedName> Rejected) Clean(string? text) =>
        Clean((text ?? "").ReplaceLineEndings("\n").Split('\n'));

    /// <summary>A name with its accents taken off ("José" -> "Jose"): Generate's reply is folded this way before it is cleaned.</summary>
    public static string Fold(string name)
    {
        var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// The names in an agent's reply: the first JSON array of strings in it (the reply may wrap it in text or a code
    /// fence), folded and cleaned. Empty when there is no such array.
    /// </summary>
    public static IReadOnlyList<string> FromReply(string? reply)
    {
        var text = reply ?? "";
        for (var start = text.IndexOf('['); start >= 0; start = text.IndexOf('[', start + 1))
        {
            for (var end = text.LastIndexOf(']'); end > start; end = text.LastIndexOf(']', end - 1))
            {
                JsonArray? array;
                try { array = JsonNode.Parse(text[start..(end + 1)]) as JsonArray; }
                catch (System.Text.Json.JsonException) { continue; }
                if (array is null) continue;
                var strings = array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => s is not null).ToList();
                if (strings.Count == 0) continue;
                return Clean(strings.Select(s => Fold(s!))).Names;
            }
        }
        return [];
    }

    /// <summary>Whether a member name is the job word alone (no space): the app adds the first name.</summary>
    public static bool IsJobWordOnly(string? name) => !(name ?? "").Trim().Contains(' ');

    /// <summary>The first-name part of a member's name: the words after the job word ("Developer Priya" -> "Priya"), or null.</summary>
    public static string? GivenNameOf(string? name)
    {
        var words = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length < 2 ? null : string.Join(' ', words.Skip(1));
    }

    /// <summary>
    /// A first name from <paramref name="list"/> no member uses: up to <see cref="PickTries"/> random tries, the first
    /// free one taken. Null when the list is empty or every try hit a used name.
    /// </summary>
    public static string? Pick(IReadOnlyList<string> list, Func<string, bool> used, Random? random = null)
    {
        if (list.Count == 0) return null;
        random ??= Random.Shared;
        for (var i = 0; i < PickTries; i++)
        {
            var candidate = list[random.Next(list.Count)];
            if (!used(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The hire refusal when no free name came from the list: the Manager calls again with a full name.</summary>
    public static string HireNoFreeName(string job, NameCountry country) =>
        $"No free name on the person's name list. Call hire again with name: '{job} <a first name common in {country.DisplayName()}>', one not used by any member of any team.";

    /// <summary>The team-creation refusal when no free name came from the list for a job word.</summary>
    public static string CreateNoFreeName(string job) =>
        $"No free name on your member-name list for '{job}'; type a full name or add names in Settings > Teams.";
}
