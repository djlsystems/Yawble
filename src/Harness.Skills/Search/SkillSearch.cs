using Harness.Contracts;

namespace Harness.Skills.Search;

/// <summary>
/// THE SKILL SEARCH SQLITE'S FTS5 INDEX ANSWERS, AS CODE, for a store with no such index. The SQLite
/// store builds its MATCH from a person's words (<see cref="SkillRules.SearchWords"/>): each word one
/// quoted phrase, the phrases ORed, over the three columns <c>skills_fts</c> indexes (name, description,
/// body) with <c>tokenize='porter unicode61'</c>. Here each word is tokenized and stemmed as FTS5 does
/// (<see cref="Unicode61"/>, <see cref="Porter"/>) into its phrase, and a skill matches when some phrase
/// with at least one term has all its terms consecutive in one of the three columns. A phrase with no
/// terms (a word of punctuation alone) matches nothing, and the other phrases still count, as in FTS5.
///
/// <para>
/// WHERE IT CAN DIFFER FROM SQLITE: only where the SQLite build differs from the tables and stemmer
/// ported here (SQLite 3.49 to 3.50.4 and master share them), and in one pointer-sized corner the stemmer
/// comment names. Ranking does not arise: the stores order by id.
/// </para>
/// </summary>
public sealed class SkillSearch
{
    private readonly List<List<string>> _phrases;

    private SkillSearch(List<List<string>> phrases) => _phrases = phrases;

    /// <summary>The search for <paramref name="query"/>, or null for none (a query of white space alone).</summary>
    public static SkillSearch? For(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        return new SkillSearch([.. SkillRules.SearchWords(query).Select(Terms)]);
    }

    /// <summary>Whether <paramref name="skill"/> matches any of the phrases.</summary>
    public bool Matches(Skill skill)
    {
        if (_phrases.All(phrase => phrase.Count == 0)) return false;

        foreach (var column in new[] { skill.Name, skill.Description, skill.Body })
        {
            var terms = Terms(column);

            foreach (var phrase in _phrases)
            {
                if (phrase.Count > 0 && Contains(terms, phrase)) return true;
            }
        }

        return false;
    }

    /// <summary>The terms FTS5 indexes <paramref name="text"/> as: each token, stemmed.</summary>
    public static List<string> Terms(string text) => [.. Unicode61.Tokenize(text).Select(Porter.Stem)];

    private static bool Contains(List<string> terms, List<string> phrase)
    {
        for (var start = 0; start + phrase.Count <= terms.Count; start++)
        {
            var all = true;
            for (var i = 0; i < phrase.Count && all; i++) all = string.Equals(terms[start + i], phrase[i], StringComparison.Ordinal);
            if (all) return true;
        }

        return false;
    }
}
