using System.Security.Cryptography;
using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// The credential-shape rule of <see cref="DiagnosticRedaction"/>: a run of 32 or more base64url
/// characters with upper case, lower case and a digit is replaced, and a hex digest behind a short
/// letter prefix - how a service names its records - is an identifier and is kept, and so is a
/// public key: a DKIM record's <c>p=</c> value and a base64 SubjectPublicKeyInfo.
/// </summary>
public sealed class DiagnosticRedactionTests
{
    private const string Hex = "3f9c2b7a1e4d8f6a0b5c9e2d7f1a4b8c";

    [Theory]
    [InlineData("SM" + Hex)] // a Twilio message
    [InlineData("AC" + Hex)] // a Twilio account
    [InlineData("MG" + Hex)] // a Twilio Messaging Service
    [InlineData("SK" + Hex)] // a Twilio API key's id
    [InlineData("evt_" + Hex)]
    [InlineData("ID-3F9C2B7A1E4D8F6A0B5C9E2D7F1A4B8C")]
    public void A_hex_digest_behind_a_short_letter_prefix_is_an_identifier_and_is_kept(string id)
    {
        var text = $"Sent {id} to +15551234567.";

        Assert.Equal(text, DiagnosticRedaction.RedactWithoutLimit(text));
    }

    [Theory]
    [InlineData("Qm7vX2pL9tRw4kZs8Hn3Yb6dJ1fA5gC0eT7uI2oP4sW")] // this product's own credential's shape
    [InlineData("SM3f9C2b7a1e4d8f6a0b5c9e2d7f1a4b8c")]          // hex of mixed case is not a digest
    [InlineData("ABCDE" + Hex)]                                   // a prefix longer than four letters
    [InlineData("key_prod_51Hq8xKz2Lm9Vb7Ty4Wn6Rp3Ds5Fg1Jh0")]     // a key behind a prefix that is not hex
    [InlineData("6RqT2mXz9LkV4bNw7HsY3pJd8FcG1aZe")]              // a Twilio API key's secret
    public void A_random_looking_run_is_still_replaced(string secret)
    {
        Assert.Equal("value [redacted] end", DiagnosticRedaction.RedactWithoutLimit($"value {secret} end"));
    }

    /// <summary>A 2048-bit RSA public key as DKIM publishes it, made here so no key-shaped literal is
    /// in the source.</summary>
    internal static string DkimPublicKey()
    {
        using var rsa = RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>A credential in this product's own shape: 43 base64url characters, made here. Drawn
    /// again until it has upper case, lower case and a digit, so the rule it is checked against
    /// applies.</summary>
    internal static string RandomCredential()
    {
        while (true)
        {
            var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (value.Any(char.IsAsciiLetterUpper) && value.Any(char.IsAsciiLetterLower) && value.Any(char.IsAsciiDigit)) return value;
        }
    }

    [Fact]
    public void A_dkim_record_in_a_namecheap_list_line_comes_through_unchanged()
    {
        var key = DkimPublicKey();
        var line = $"TXT default._domainkey \"v=DKIM1; k=rsa; p={key}\" TTL 1799";

        Assert.StartsWith("MIIBIjANBgkq", key, StringComparison.Ordinal);
        Assert.Equal(line, DiagnosticRedaction.RedactWithoutLimit(line));
    }

    [Fact]
    public void A_dkim_record_on_its_own_line_unquoted_comes_through_unchanged()
    {
        var text = $"default._domainkey IN TXT v=DKIM1; t=s; p={DkimPublicKey()}\nnext line";

        Assert.Equal(text, DiagnosticRedaction.RedactWithoutLimit(text));
    }

    [Fact]
    public void A_credential_beside_a_dkim_record_is_still_redacted()
    {
        var key = DkimPublicKey();
        var credential = RandomCredential();
        var line = $"TXT default._domainkey \"v=DKIM1; k=rsa; p={key}\" token {credential}";

        Assert.Equal(
            $"TXT default._domainkey \"v=DKIM1; k=rsa; p={key}\" token {DiagnosticRedaction.Placeholder}",
            DiagnosticRedaction.RedactWithoutLimit(line));
    }

    [Fact]
    public void A_credential_in_a_record_that_is_not_dkim_is_still_redacted()
    {
        var credential = RandomCredential();

        Assert.Equal(
            $"TXT _acme \"v=spf1; p={DiagnosticRedaction.Placeholder}\"",
            DiagnosticRedaction.RedactWithoutLimit($"TXT _acme \"v=spf1; p={credential}\""));
    }

    [Fact]
    public void A_base64_public_key_outside_a_dkim_record_is_kept()
    {
        using var small = RSA.Create(1024);
        using var large = RSA.Create(4096);
        var keys = new[] { DkimPublicKey(), Convert.ToBase64String(small.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(large.ExportSubjectPublicKeyInfo()) };

        Assert.Equal(["MIIB", "MIGf", "MIIC"], keys.Select(key => key[..4]));
        foreach (var key in keys)
        {
            var text = $"public key {key} end";
            Assert.Equal(text, DiagnosticRedaction.RedactWithoutLimit(text));
        }
    }

    [Fact]
    public void A_run_that_starts_like_a_public_key_and_does_not_decode_to_one_is_still_redacted()
    {
        var lookalike = "MIIB" + RandomCredential();

        Assert.Equal("value [redacted] end", DiagnosticRedaction.RedactWithoutLimit($"value {lookalike} end"));
    }
}
