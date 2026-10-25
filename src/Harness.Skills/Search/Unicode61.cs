using System.Text;

namespace Harness.Skills.Search;

/// <summary>
/// SQLITE'S <c>unicode61</c> TOKENIZER WITH ITS DEFAULTS (<c>categories 'L* N* Co'</c>,
/// <c>remove_diacritics 1</c>), ported line for line from SQLite's public-domain
/// <c>ext/fts5/fts5_tokenize.c</c> (<c>fts5UnicodeTokenize</c>) and <c>fts5_unicode2.c</c>, over the same
/// tables (<see cref="Unicode61Tables"/>). Not .NET's own Unicode data: SQLite's tables are frozen at the
/// Unicode version they were generated from, and a letter .NET knows that they do not must split a word
/// here exactly as it does there.
///
/// <para>
/// A token is the run of token characters (the categories above, plus a code point the tables do not
/// cover, which SQLite counts as category 0 and so as a token character), each lower-cased by SQLite's
/// fold table and stripped of a diacritic by its simple table; a combining mark inside a token is kept
/// and folds to nothing. Tokens come out as SQLite's bytes: the UTF-8 of the folded text, each byte one
/// <see cref="char"/> (Latin-1), so the stemmer works on the bytes SQLite's does and two tokens compare
/// as SQLite compares its terms.
/// </para>
/// </summary>
public static class Unicode61
{
    /// <summary><c>aCategory</c> for <c>L* N* Co</c>: what <c>sqlite3Fts5UnicodeCatParse</c> sets, index 0 included.</summary>
    private static readonly bool[] TokenCategory = Categories();

    /// <summary><c>aTokenChar</c>: the ASCII token characters, as <c>sqlite3Fts5UnicodeAscii</c> derives them.</summary>
    private static readonly bool[] AsciiToken = Ascii();

    /// <summary>The tokens of <paramref name="text"/>, in order, each as SQLite's folded UTF-8 bytes.</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var codes = CodePoints(text);
        var i = 0;
        var token = new StringBuilder();

        while (true)
        {
            // Skip any separator characters.
            while (true)
            {
                if (i >= codes.Count) return tokens;
                var c = codes[i];
                if (c >= 0x80 ? IsAlnum(c) : AsciiToken[c]) break;
                i++;
            }

            token.Clear();

            // Run through the token characters, folding them into the token.
            while (i < codes.Count)
            {
                var c = codes[i];

                if (c >= 0x80)
                {
                    if (!IsAlnum(c) && !IsDiacritic(c)) break;
                    var folded = Fold(c, removeDiacritic: true);
                    if (folded != 0) WriteUtf8(token, folded);
                }
                else
                {
                    if (!AsciiToken[c]) break;
                    token.Append((char)(c is >= 'A' and <= 'Z' ? c + 32 : c));
                }

                i++;
            }

            tokens.Add(token.ToString());
        }
    }

    /// <summary>
    /// The code points SQLite reads from the UTF-8 it is given: a lone surrogate is already U+FFFD in
    /// that UTF-8, and <c>FTS5_READ_UTF8</c> reads U+FFFE and U+FFFF as U+FFFD too.
    /// </summary>
    private static List<int> CodePoints(string text)
    {
        var codes = new List<int>(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            var c = rune.Value;
            codes.Add(c is 0xFFFE or 0xFFFF ? 0xFFFD : c);
        }

        return codes;
    }

    /// <summary><c>FTS5_WRITE_UTF8</c>, one byte per char.</summary>
    private static void WriteUtf8(StringBuilder output, int c)
    {
        if (c < 0x80)
        {
            output.Append((char)c);
        }
        else if (c < 0x800)
        {
            output.Append((char)(0xC0 + ((c >> 6) & 0x1F)));
            output.Append((char)(0x80 + (c & 0x3F)));
        }
        else if (c < 0x10000)
        {
            output.Append((char)(0xE0 + ((c >> 12) & 0x0F)));
            output.Append((char)(0x80 + ((c >> 6) & 0x3F)));
            output.Append((char)(0x80 + (c & 0x3F)));
        }
        else
        {
            output.Append((char)(0xF0 + ((c >> 18) & 0x07)));
            output.Append((char)(0x80 + ((c >> 12) & 0x3F)));
            output.Append((char)(0x80 + ((c >> 6) & 0x3F)));
            output.Append((char)(0x80 + (c & 0x3F)));
        }
    }

    /// <summary><c>fts5UnicodeIsAlnum</c> with no tokenchars or separators exceptions.</summary>
    private static bool IsAlnum(int c) => TokenCategory[Category((uint)c)];

    /// <summary><c>sqlite3Fts5UnicodeCategory</c>.</summary>
    internal static int Category(uint code)
    {
        if (code >= 1 << 20) return 0;

        var lo = (int)Unicode61Tables.Block[code >> 16];
        var hi = (int)Unicode61Tables.Block[1 + (code >> 16)];
        var key = (ushort)(code & 0xFFFF);
        var res = -1;

        while (hi > lo)
        {
            var test = (hi + lo) / 2;
            if (key >= Unicode61Tables.Map[test])
            {
                res = test;
                lo = test + 1;
            }
            else
            {
                hi = test;
            }
        }

        if (res < 0) return 0;
        if (key >= Unicode61Tables.Map[res] + (Unicode61Tables.Data[res] >> 5)) return 0;

        var category = Unicode61Tables.Data[res] & 0x1F;
        if (category != 30) return category;
        return ((key - Unicode61Tables.Map[res]) & 0x01) != 0 ? 5 : 9;
    }

    /// <summary><c>sqlite3Fts5UnicodeIsdiacritic</c>.</summary>
    internal static bool IsDiacritic(int c)
    {
        const uint mask0 = 0x08029FDF;
        const uint mask1 = 0x000361F8;
        if (c < 768 || c > 817) return false;
        return c < 768 + 32
            ? (mask0 & (1u << (c - 768))) != 0
            : (mask1 & (1u << (c - 768 - 32))) != 0;
    }

    /// <summary><c>sqlite3Fts5UnicodeFold</c>.</summary>
    internal static int Fold(int c, bool removeDiacritic)
    {
        var ret = c;

        if (c < 128)
        {
            if (c is >= 'A' and <= 'Z') ret = c + ('a' - 'A');
        }
        else if (c < 65536)
        {
            var entries = Unicode61Tables.Fold;
            var hi = entries.Length - 1;
            var lo = 0;
            var res = -1;

            while (hi >= lo)
            {
                var test = (hi + lo) / 2;
                if (c - entries[test].Code >= 0)
                {
                    res = test;
                    lo = test + 1;
                }
                else
                {
                    hi = test - 1;
                }
            }

            var entry = entries[res];
            if (c < entry.Code + entry.Range && (0x01 & entry.Flags & (entry.Code ^ c)) == 0)
            {
                ret = (c + Unicode61Tables.FoldOffset[entry.Flags >> 1]) & 0x0000FFFF;
            }

            if (removeDiacritic) ret = RemoveDiacritic(ret, complex: false);
        }
        else if (c is >= 66560 and < 66600)
        {
            ret = c + 40;
        }

        return ret;
    }

    /// <summary><c>fts5_remove_diacritic</c>.</summary>
    private static int RemoveDiacritic(int c, bool complex)
    {
        var dia = Unicode61Tables.Dia;
        var key = ((uint)c << 3) | 0x00000007;
        var res = 0;
        var hi = dia.Length - 1;
        var lo = 0;

        while (hi >= lo)
        {
            var test = (hi + lo) / 2;
            if (key >= dia[test])
            {
                res = test;
                lo = test + 1;
            }
            else
            {
                hi = test - 1;
            }
        }

        if (!complex && (Unicode61Tables.DiaChar[res] & 0x80) != 0) return c;
        return c > (dia[res] >> 3) + (dia[res] & 0x07) ? c : Unicode61Tables.DiaChar[res] & 0x7F;
    }

    private static bool[] Categories()
    {
        var categories = new bool[32];
        categories[0] = true;
        foreach (var index in new[] { 5, 6, 7, 8, 9, 30, 13, 14, 15, 31 }) categories[index] = true;
        return categories;
    }

    private static bool[] Ascii()
    {
        var ascii = new bool[128];
        var i = 0;
        var table = 0;

        while (i < 128)
        {
            var token = TokenCategory[Unicode61Tables.Data[table] & 0x1F];
            var n = (Unicode61Tables.Data[table] >> 5) + i;
            for (; i < 128 && i < n; i++) ascii[i] = token;
            table++;
        }

        ascii[0] = false;
        return ascii;
    }
}
