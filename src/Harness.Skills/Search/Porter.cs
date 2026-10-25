namespace Harness.Skills.Search;

/// <summary>
/// A port of SQLite's public-domain Porter stemmer (<c>fts5PorterCb</c> and its steps in
/// <c>ext/fts5/fts5_tokenize.c</c>), step for step, on the same bytes: a token from
/// <see cref="Unicode61.Tokenize"/> is UTF-8 one byte per char, and only ASCII letters are vowels, so a
/// non-ASCII byte is a consonant here as it is there. A token shorter than 3 bytes or longer than 64
/// passes through unstemmed (<c>FTS5_PORTER_MAX_TOKEN</c>).
///
/// <para>
/// ONE READ BEFORE THE BUFFER. A token like <c>aed</c> stems to one byte, and SQLite's next step then
/// reads the byte before its buffer, the top byte of the pointer stored just ahead of it, which is zero on
/// the 64-bit hosts SQLite runs on here. <see cref="At"/> answers zero for it.
/// </para>
/// </summary>
public static class Porter
{
    public const int MaxToken = 64;

    public static string Stem(string token)
    {
        if (token.Length > MaxToken || token.Length < 3) return token;

        var buf = new char[MaxToken + 64];
        token.CopyTo(0, buf, 0, token.Length);
        var n = token.Length;

        // Step 1.
        Step1A(buf, ref n);
        if (Step1B(buf, ref n))
        {
            if (!Step1B2(buf, ref n))
            {
                var c = At(buf, n - 1);
                if (!IsVowel(c, false) && c != 'l' && c != 's' && c != 'z' && c == At(buf, n - 2))
                {
                    n--;
                }
                else if (MEq1(buf, 0, n) && OStar(buf, n))
                {
                    buf[n++] = 'e';
                }
            }
        }

        // Step 1C.
        if (At(buf, n - 1) == 'y' && Vowel(buf, n - 1)) buf[n - 1] = 'i';

        // Steps 2 through 4.
        Step2(buf, ref n);
        Step3(buf, ref n);
        Step4(buf, ref n);

        // Step 5a.
        if (At(buf, n - 1) == 'e' && (MGt1(buf, 0, n - 1) || (MEq1(buf, 0, n - 1) && !OStar(buf, n - 1)))) n--;

        // Step 5b.
        if (n > 1 && buf[n - 1] == 'l' && buf[n - 2] == 'l' && MGt1(buf, 0, n - 1)) n--;

        return new string(buf, 0, n);
    }

    private static char At(char[] buf, int i) => i < 0 ? '\0' : buf[i];

    private static bool IsVowel(char c, bool yIsVowel) =>
        c is 'a' or 'e' or 'i' or 'o' or 'u' || (yIsVowel && c == 'y');

    /// <summary><c>fts5PorterGobbleVC</c> over <paramref name="buf"/>[<paramref name="start"/>..start+<paramref name="n"/>].</summary>
    private static int GobbleVC(char[] buf, int start, int n, bool prevCons)
    {
        var cons = prevCons;
        int i;

        for (i = 0; i < n; i++)
        {
            cons = !IsVowel(buf[start + i], cons);
            if (!cons) break;
        }

        for (i++; i < n; i++)
        {
            cons = !IsVowel(buf[start + i], cons);
            if (cons) return i + 1;
        }

        return 0;
    }

    private static bool MGt0(char[] buf, int start, int n) => GobbleVC(buf, start, n, false) != 0;

    private static bool MGt1(char[] buf, int start, int n)
    {
        var g = GobbleVC(buf, start, n, false);
        return g != 0 && GobbleVC(buf, start + g, n - g, true) != 0;
    }

    private static bool MEq1(char[] buf, int start, int n)
    {
        var g = GobbleVC(buf, start, n, false);
        return g != 0 && GobbleVC(buf, start + g, n - g, true) == 0;
    }

    private static bool OStar(char[] buf, int n)
    {
        var last = At(buf, n - 1);
        if (last is 'w' or 'x' or 'y') return false;

        var mask = 0;
        var cons = false;
        for (var i = 0; i < n; i++)
        {
            cons = !IsVowel(buf[i], cons);
            mask = (mask << 1) + (cons ? 1 : 0);
        }

        return (mask & 0x0007) == 0x0005;
    }

    private static bool MGt1AndSOrT(char[] buf, int n) =>
        (At(buf, n - 1) == 's' || At(buf, n - 1) == 't') && MGt1(buf, 0, n);

    private static bool Vowel(char[] buf, int n)
    {
        for (var i = 0; i < n; i++)
        {
            if (IsVowel(buf[i], i > 0)) return true;
        }

        return false;
    }

    private static bool EndsWith(char[] buf, int n, string suffix)
    {
        if (n <= suffix.Length) return false;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (buf[n - suffix.Length + i] != suffix[i]) return false;
        }

        return true;
    }

    private static void Replace(char[] buf, ref int n, string suffix, string output)
    {
        var stem = n - suffix.Length;
        output.CopyTo(0, buf, stem, output.Length);
        n = stem + output.Length;
    }

    private enum Condition { MGt0, MGt1, MGt1SOrT }

    private static bool Holds(Condition condition, char[] buf, int stem) => condition switch
    {
        Condition.MGt0 => MGt0(buf, 0, stem),
        Condition.MGt1 => MGt1(buf, 0, stem),
        _ => MGt1AndSOrT(buf, stem),
    };

    /// <summary>
    /// One generated step: under the case of the byte before the last, the first rule whose suffix the
    /// token ends with (and is longer than) is the one tried, and its condition decides; no later rule is.
    /// </summary>
    private static void Apply(char[] buf, ref int n, (char Case, string Suffix, Condition When, string Output)[] rules)
    {
        var key = At(buf, n - 2);

        foreach (var rule in rules)
        {
            if (rule.Case != key || !EndsWith(buf, n, rule.Suffix)) continue;
            if (Holds(rule.When, buf, n - rule.Suffix.Length)) Replace(buf, ref n, rule.Suffix, rule.Output);
            return;
        }
    }

    private static readonly (char, string, Condition, string)[] Step4Rules =
    [
        ('a', "al", Condition.MGt1, ""),
        ('c', "ance", Condition.MGt1, ""), ('c', "ence", Condition.MGt1, ""),
        ('e', "er", Condition.MGt1, ""),
        ('i', "ic", Condition.MGt1, ""),
        ('l', "able", Condition.MGt1, ""), ('l', "ible", Condition.MGt1, ""),
        ('n', "ant", Condition.MGt1, ""), ('n', "ement", Condition.MGt1, ""), ('n', "ment", Condition.MGt1, ""), ('n', "ent", Condition.MGt1, ""),
        ('o', "ion", Condition.MGt1SOrT, ""), ('o', "ou", Condition.MGt1, ""),
        ('s', "ism", Condition.MGt1, ""),
        ('t', "ate", Condition.MGt1, ""), ('t', "iti", Condition.MGt1, ""),
        ('u', "ous", Condition.MGt1, ""),
        ('v', "ive", Condition.MGt1, ""),
        ('z', "ize", Condition.MGt1, ""),
    ];

    private static readonly (char, string, Condition, string)[] Step2Rules =
    [
        ('a', "ational", Condition.MGt0, "ate"), ('a', "tional", Condition.MGt0, "tion"),
        ('c', "enci", Condition.MGt0, "ence"), ('c', "anci", Condition.MGt0, "ance"),
        ('e', "izer", Condition.MGt0, "ize"),
        ('g', "logi", Condition.MGt0, "log"),
        ('l', "bli", Condition.MGt0, "ble"), ('l', "alli", Condition.MGt0, "al"), ('l', "entli", Condition.MGt0, "ent"),
        ('l', "eli", Condition.MGt0, "e"), ('l', "ousli", Condition.MGt0, "ous"),
        ('o', "ization", Condition.MGt0, "ize"), ('o', "ation", Condition.MGt0, "ate"), ('o', "ator", Condition.MGt0, "ate"),
        ('s', "alism", Condition.MGt0, "al"), ('s', "iveness", Condition.MGt0, "ive"), ('s', "fulness", Condition.MGt0, "ful"),
        ('s', "ousness", Condition.MGt0, "ous"),
        ('t', "aliti", Condition.MGt0, "al"), ('t', "iviti", Condition.MGt0, "ive"), ('t', "biliti", Condition.MGt0, "ble"),
    ];

    private static readonly (char, string, Condition, string)[] Step3Rules =
    [
        ('a', "ical", Condition.MGt0, "ic"),
        ('s', "ness", Condition.MGt0, ""),
        ('t', "icate", Condition.MGt0, "ic"), ('t', "iciti", Condition.MGt0, "ic"),
        ('u', "ful", Condition.MGt0, ""),
        ('v', "ative", Condition.MGt0, ""),
        ('z', "alize", Condition.MGt0, "al"),
    ];

    private static void Step2(char[] buf, ref int n) => Apply(buf, ref n, Step2Rules);

    private static void Step3(char[] buf, ref int n) => Apply(buf, ref n, Step3Rules);

    private static void Step4(char[] buf, ref int n) => Apply(buf, ref n, Step4Rules);

    /// <summary><c>fts5PorterStep1B2</c>: true when it rewrote the ending.</summary>
    private static bool Step1B2(char[] buf, ref int n)
    {
        foreach (var (key, suffix, output) in new[] { ('a', "at", "ate"), ('b', "bl", "ble"), ('i', "iz", "ize") })
        {
            if (At(buf, n - 2) != key) continue;
            if (!EndsWith(buf, n, suffix)) return false;
            Replace(buf, ref n, suffix, output);
            return true;
        }

        return false;
    }

    /// <summary><c>fts5PorterStep1B</c>: true when it removed <c>ed</c> or <c>ing</c>.</summary>
    private static bool Step1B(char[] buf, ref int n)
    {
        switch (At(buf, n - 2))
        {
            case 'e':
                if (EndsWith(buf, n, "eed"))
                {
                    if (MGt0(buf, 0, n - 3)) Replace(buf, ref n, "eed", "ee");
                }
                else if (EndsWith(buf, n, "ed"))
                {
                    if (Vowel(buf, n - 2))
                    {
                        n -= 2;
                        return true;
                    }
                }

                break;

            case 'n':
                if (EndsWith(buf, n, "ing") && Vowel(buf, n - 3))
                {
                    n -= 3;
                    return true;
                }

                break;
        }

        return false;
    }

    /// <summary><c>fts5PorterStep1A</c>.</summary>
    private static void Step1A(char[] buf, ref int n)
    {
        if (At(buf, n - 1) != 's') return;

        if (At(buf, n - 2) == 'e')
        {
            n = (n > 4 && buf[n - 4] == 's' && buf[n - 3] == 's') || (n > 3 && buf[n - 3] == 'i') ? n - 2 : n - 1;
        }
        else if (At(buf, n - 2) != 's')
        {
            n -= 1;
        }
    }
}
