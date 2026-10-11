using System.Globalization;

namespace Harness.Contracts;

/// <summary>
/// The human-facing citation for one PLATFORM backlog item: <c>B000H</c> for item 17.
///
/// <para>
/// <b>THE BODY IS CROCKFORD BASE 32, UPPERCASE, ZERO-PADDED TO FOUR CHARACTERS.</b>
/// The alphabet is <c>0123456789ABCDEFGHJKMNPQRSTVWXYZ</c> - digits and letters minus <c>I</c>,
/// <c>L</c>, <c>O</c> and <c>U</c>. Four characters hold 1,048,576 ids. Because the alphabet is a
/// strict, ordered subset of <c>0-9A-Z</c> in ASCII, a padded uppercase id SORTS LEXICALLY IN
/// NUMERIC ORDER: <c>B000H</c> (17) before <c>B0034</c> (100), in a file listing, a folder of
/// spec files, a <c>git log --name-only</c>. That property is the reason this exists, and it is why
/// the output is always uppercase and always padded: lowercase sorts after <c>Z</c> and breaks it.
/// </para>
///
/// <para>
/// <b>ABOVE 1,048,575 THE BODY WIDENS</b> to five or more characters rather than failing or
/// wrapping: <c>B10000</c> for 1,048,576, up to thirteen characters for <c>long.MaxValue</c>.
/// Every such id still round-trips through <see cref="TryParse"/>. What is NOT promised beyond the
/// four-character space is the sort property - <c>B10000</c> sorts before <c>BZZZZ</c> as a
/// string - and that is accepted: over a million items is far past anything a backlog will hold,
/// and the alternative, a fifth character on every id, looks bureaucratic on a screen where most
/// ids are two digits.
/// </para>
///
/// <para>
/// <b>PARSING: WIDTH DECIDES THE BASE.</b> <see cref="TryParse"/> trims, strips a single leading
/// <c>B</c>/<c>b</c>, and then looks at the body's LENGTH:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Four characters or more</b> decode as Crockford base 32, case-insensitively, with
///     Crockford's forgiving glyphs: <c>I</c> and <c>L</c> read as <c>1</c>, <c>O</c> reads as
///     <c>0</c>, so a person who types <c>B0O1H</c> off a screenshot gets <c>B001H</c>.
///     <c>U</c> is not in the alphabet and is refused.
///   </description></item>
///   <item><description>
///     <b>One to three characters</b> decode as base 10: <c>B11</c> is eleven and a bare <c>17</c>
///     is seventeen, which is how a person naturally cites a small id.
///   </description></item>
/// </list>
/// <para>
/// A bare number with no prefix works either way, which is what an agent hands back after reading
/// an id out of a payload. The result must be positive; anything else - empty, <c>B</c> alone,
/// zero, a sign, a character outside the alphabet, an overflow - answers false and never throws.
/// </para>
///
/// <para>
/// <b>THE KNOWN WEAKNESS, ACCEPTED ON PURPOSE:</b> a human who writes <c>B0010</c> meaning ten
/// gets thirty-two, because four characters is the Crockford shape and <c>0010</c> is a valid one.
/// It is wrong without being an error. The alternative - a second prefix so the two alphabets can
/// never be confused - is a second spelling of one fact, which this codebase refuses.
/// The same rule has a second face: ANY four or more characters from the alphabet is an id, so a
/// word made of them - <c>zero</c>, <c>seventeen</c> - parses rather than being refused. A caller
/// that wants to tell a word from an id has to ask the store whether the item exists.
/// </para>
///
/// <para>
/// This is a rendering and parsing concern only. The stored id is an integer, the HTTP route is
/// <c>{id:long}</c> and the JSON <c>id</c> is a number.
/// </para>
/// </summary>
public static class PlatformBacklogId
{
    /// <summary>What <see cref="Format"/> writes.</summary>
    public const string Prefix = "B";

    /// <summary>Crockford's alphabet: digits and letters minus I, L, O and U. Index is the value.</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>The rendered body is padded to this many characters; a wider body decodes the same way.</summary>
    public const int Width = 4;

    private const int Bits = 5;
    private const int Mask = (1 << Bits) - 1;

    public static string Format(long id)
    {
        Span<char> buffer = stackalloc char[13]; // ceil(63 / 5): long.MaxValue is 7ZZZZZZZZZZZZ
        var position = buffer.Length;
        var remaining = id;

        do
        {
            buffer[--position] = Alphabet[(int)(remaining & Mask)];
            remaining >>>= Bits;
        }
        while (remaining != 0);

        var body = buffer[position..];
        return body.Length >= Width
            ? string.Concat(Prefix, body)
            : string.Concat(Prefix, new string('0', Width - body.Length), body);
    }

    public static bool TryParse(string? raw, out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var body = raw.AsSpan().Trim();

        if (body[0] is 'B' or 'b')
        {
            body = body[1..];
        }

        if (body.IsEmpty) return false;

        if (body.Length < Width)
        {
            // Base 10, digits only: no sign, no whitespace, no thousands separator. `NumberStyles.None`
            // is what keeps `B-1` and `+5` out.
            return long.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }

        return TryDecodeCrockford(body, out id) && id > 0;
    }

    private static bool TryDecodeCrockford(ReadOnlySpan<char> body, out long value)
    {
        value = 0;
        const long limit = long.MaxValue >> Bits;

        foreach (var glyph in body)
        {
            var digit = DigitOf(glyph);
            if (digit < 0) { value = 0; return false; }

            // Shifting past this would wrap silently into a DIFFERENT valid id, which is the one
            // failure the whole scheme exists to avoid.
            if (value > limit) { value = 0; return false; }
            value = (value << Bits) | (long)digit;
        }

        return true;
    }

    /// <summary>
    /// The value of one glyph, or -1. Case folds; I and L are 1, O is 0, per Crockford. U is
    /// deliberately absent from the alphabet and stays absent here.
    /// </summary>
    private static int DigitOf(char glyph)
    {
        var upper = char.ToUpperInvariant(glyph);
        return upper switch
        {
            'I' or 'L' => 1,
            'O' => 0,
            _ => upper < 128 ? Alphabet.IndexOf(upper) : -1,
        };
    }
}
