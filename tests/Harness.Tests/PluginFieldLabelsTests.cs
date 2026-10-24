using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A MANIFEST'S SECRET FIELD TAKES <c>masked</c> AND <c>label</c>, AND A CONFIG FIELD TAKES
/// <c>label</c>. <c>masked</c> is true unless it says false, and anything but true or false is
/// refused naming the field; a <c>label</c> is a short line of text, refused naming the field when
/// it is empty, too long, several lines or not text. An unknown key in a field stays ignored.
/// </summary>
public sealed class PluginFieldLabelsTests
{
    private static (PluginManifest? Manifest, string? Refusal) Parse(string secrets, string config = "{}") => PluginManifest.Parse(
        $$"""
        {
          "schemaVersion": 1, "id": "labelled", "name": "Labelled", "description": "A fixture.", "version": "1.0.0",
          "protocol": "harness.member/1", "executable": { "path": "bin/run" },
          "config": {{config}},
          "secrets": {{secrets}}
        }
        """);

    [Fact]
    public void Masked_and_label_are_read_and_masked_is_true_unless_it_says_false()
    {
        var (manifest, refusal) = Parse(
            """
            {
              "apiKey": { "description": "The key.", "required": true, "label": "API key" },
              "userName": { "description": "The account.", "masked": false, "label": "User name", "someLaterKey": 1 },
              "other": { "masked": true }
            }
            """,
            """{ "clientIp": { "type": "string", "label": "Client IP" }, "plain": { "type": "string" } }""");

        Assert.Null(refusal);
        Assert.True(manifest!.Secrets["apiKey"].Masked);
        Assert.Equal("API key", manifest.Secrets["apiKey"].Label);
        Assert.False(manifest.Secrets["userName"].Masked);
        Assert.Equal("User name", manifest.Secrets["userName"].Label);
        Assert.True(manifest.Secrets["other"].Masked);
        Assert.Null(manifest.Secrets["other"].Label);
        Assert.Equal("Client IP", manifest.Config["clientIp"].Label);
        Assert.Null(manifest.Config["plain"].Label);
    }

    [Theory]
    [InlineData("\"no\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void Masked_that_is_not_true_or_false_is_refused_naming_the_field(string masked)
    {
        var (manifest, refusal) = Parse($$"""{ "apiKey": { "masked": {{masked}} } }""");

        Assert.Null(manifest);
        Assert.Equal("`secrets.apiKey.masked` must be true or false.", refusal);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("3")]
    [InlineData("\"two\\nlines\"")]
    [InlineData("\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    public void A_label_that_is_not_a_short_line_of_text_is_refused_naming_the_field(string label)
    {
        var (secretManifest, secretRefusal) = Parse($$"""{ "apiKey": { "label": {{label}} } }""");
        Assert.Null(secretManifest);
        Assert.Equal("`secrets.apiKey.label` must be a short piece of text (1 to 80 characters, one line).", secretRefusal);

        var (configManifest, configRefusal) = Parse("{}", $$"""{ "clientIp": { "type": "string", "label": {{label}} } }""");
        Assert.Null(configManifest);
        Assert.Equal("`config.clientIp.label` must be a short piece of text (1 to 80 characters, one line).", configRefusal);
    }
}
