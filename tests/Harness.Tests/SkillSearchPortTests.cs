using System.Text;
using Harness.Skills.Search;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// THE PORTED TOKENIZER AND STEMMER INDEX TEXT AS THE SQLITE THIS BUILD SHIPS DOES. A fixed, seeded corpus
/// (English words with every Porter suffix, punctuation, Latin, Greek, Cyrillic, CJK, combining marks,
/// emoji, private-use and unassigned code points, tokens at and past 64 bytes) goes into a real FTS5 table
/// with <c>tokenize='porter unicode61'</c>, and every row's terms, read back byte for byte through
/// <c>fts5vocab</c>, must be exactly <see cref="SkillSearch.Terms"/>' for the same text. The SQL Server skill
/// store searches with the port, so a SQLite upgrade that changed its tables would fail here, by row.
/// </summary>
public sealed class SkillSearchPortTests
{
    private static readonly string[] Stems =
    [
        "run", "skill", "deploy", "connect", "relat", "happ", "hop", "fil", "agree", "gener", "nation", "condition",
        "formal", "sens", "electr", "adjust", "depend", "adopt", "activ", "effect", "cry", "sky", "tri", "cat", "feed",
        "plaster", "sing", "conflat", "troubl", "siz", "hope", "tann", "fall", "hiss", "fizz", "fail", "probat", "rate",
        "ceas", "control", "roll", "a", "ae", "y", "by", "oy", "xy", "ab", "work", "worktree", "caf",
    ];

    private static readonly string[] Suffixes =
    [
        "", "s", "es", "ies", "sses", "ed", "ing", "ly", "ness", "ful", "ational", "tional", "enci", "anci", "izer", "bli",
        "alli", "entli", "eli", "ousli", "ization", "ation", "ator", "alism", "iveness", "fulness", "ousness", "aliti",
        "iviti", "biliti", "logi", "icate", "ative", "alize", "iciti", "ical", "al", "ance", "ence", "er", "ic", "able",
        "ible", "ant", "ement", "ment", "ent", "ion", "sion", "tion", "ou", "ism", "ate", "iti", "ous", "ive", "ize", "eed",
        "y", "e", "ll", "at", "bl", "iz", "ated", "izing", "bling",
    ];

    private static readonly string[] Characters =
    [
        .. "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".Select(c => c.ToString()),
        .. " -_%[]'\".,:;!?/\\\t\n@#$^&*()+=~`|<>{}".Select(c => c.ToString()),
        .. "éèêëàâäçñöüßÉÀÇÑÖÜÆæøØåÅœŒıİſﬁ".Select(c => c.ToString()),
        .. "αβγδεζηθικλμνξοπρστυφχψωΑΒΓΔΕΖΗΘΣΩάέήίόύώΐ".Select(c => c.ToString()),
        .. "абвгдежзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙ".Select(c => c.ToString()),
        .. "\u65e5\u672c\u8a9e\u4e2d\u6587\ud55c\uad6d\uc5b4\u3072\u3089\u304c\u306a\u30ab\u30bf\u30ab\u30ca".Select(c => c.ToString()),
        "\u0301", "\u0300", "\u0308", "\u0327", "\u0303", "\u200b", "\u00a0", "\u3000", "\ue000", "\uf8ff",
        "\U0001F680", "\U0001F9EA", "\u0378", "\u0560", "\U000E0001", "\U00010400", "\U00010428", "\u2160",
        "\u00b2", "\u0660", "\u02b0", "\u01c5", "\u1e9e", "\uff21", "\uff41", "\u0130", "\u212a", "\u2126",
        "\ufffd", "\u0600", "\u00ad", "\u2028", "\u0e01", "\u0e31", "\u094d", "\u0915",
    ];

    private static IReadOnlyList<string> Corpus()
    {
        var random = new Random(8);
        var corpus = new List<string>();

        for (var i = 0; i < 1500; i++)
        {
            var word = Stems[random.Next(Stems.Length)] + Suffixes[random.Next(Suffixes.Length)];
            corpus.Add(random.Next(4) switch { 0 => word.ToUpperInvariant(), 1 => char.ToUpperInvariant(word[0]) + word[1..], _ => word });
        }

        for (var i = 0; i < 1500; i++)
        {
            var text = new StringBuilder();
            for (var n = random.Next(1, 40); n > 0; n--) text.Append(Characters[random.Next(Characters.Length)]);
            corpus.Add(text.ToString());
        }

        corpus.AddRange(
        [
            new string('x', 64), new string('x', 65), new string('a', 61) + "ing", new string('a', 62) + "ing",
            string.Concat(Enumerable.Repeat("é", 32)) + "s", "aed", "aing", "ying", "eed", "sss", "Straße", "ΑΘΗΝΑ λόγος",
        ]);

        return corpus;
    }

    [Fact]
    public async Task The_port_indexes_every_corpus_row_as_the_shipped_sqlite_does()
    {
        var ct = TestContext.Current.CancellationToken;
        var corpus = Corpus();

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE VIRTUAL TABLE t USING fts5(c, tokenize='porter unicode61');
                CREATE VIRTUAL TABLE v USING fts5vocab(t, 'instance');
                """;
            await create.ExecuteNonQueryAsync(ct);
        }

        for (var i = 0; i < corpus.Count; i++)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO t (rowid, c) VALUES ($id, $c)";
            insert.Parameters.AddWithValue("$id", i + 1);
            insert.Parameters.AddWithValue("$c", corpus[i]);
            await insert.ExecuteNonQueryAsync(ct);
        }

        var indexed = corpus.Select(_ => new List<string>()).ToList();

        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT doc, CAST(term AS BLOB) FROM v ORDER BY doc, offset";
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                indexed[(int)reader.GetInt64(0) - 1].Add(Encoding.Latin1.GetString((byte[])reader.GetValue(1)));
            }
        }

        var differ = Enumerable.Range(0, corpus.Count)
            .Where(i => !indexed[i].SequenceEqual(SkillSearch.Terms(corpus[i])))
            .Select(i => $"'{corpus[i]}': SQLite [{string.Join(", ", indexed[i])}], port [{string.Join(", ", SkillSearch.Terms(corpus[i]))}]")
            .Take(10)
            .ToList();

        Assert.True(differ.Count == 0, string.Join("\n", differ));
        Assert.True(indexed.Sum(terms => terms.Count) > corpus.Count, "The corpus indexed almost nothing; the comparison would prove little.");
    }
}
