using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// The credential-shape rule of <see cref="DiagnosticRedaction"/>: a run of 32 or more base64url
/// characters with upper case, lower case and a digit is replaced, and a hex digest behind a short
/// letter prefix - how a service names its records - is an identifier and is kept.
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
}
