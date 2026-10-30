using System.Text.Json;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A table column's <c>"format": "url"</c>: a cell written as a bare address (<c>mysite.com</c>) is
/// saved with <c>https://</c> before it; a value with a scheme is kept exactly (http stays http); a
/// path and text that is not an address are left as written, so the plugin's own check still refuses
/// them. A column without the format, such as Other hosts, is never given a scheme.
/// </summary>
public sealed class PluginTableUrlFormatTests
{
    private const string Entries = """
        {
          "type": "table",
          "key": "name",
          "columns": [
            { "name": "name", "type": "string", "required": true },
            { "name": "siteUrl", "type": "string", "format": "url" },
            { "name": "signInUrl", "type": "string", "format": "url" },
            { "name": "hosts", "type": "string" },
            { "name": "status", "type": "string", "readOnly": true }
          ]
        }
        """;

    private static PluginTable Table(string json = Entries)
    {
        var (field, refusal) = PluginConfigField.Parse("entries", JsonDocument.Parse(json).RootElement);
        Assert.True(refusal is null, refusal);
        return field!.Table!;
    }

    private static string? Refusal(string column) =>
        PluginConfigField.Parse("entries", JsonDocument.Parse($$"""
            { "type": "table", "key": "name", "columns": [ { "name": "name", "type": "string" }, {{column}} ] }
            """).RootElement).Refusal;

    [Theory]
    [InlineData("mysite.com", "https://mysite.com")]
    [InlineData("www.mysite.com", "https://www.mysite.com")]
    [InlineData("  mysite.com  ", "https://mysite.com")]
    [InlineData("app.mysite.co.uk/login?next=/home", "https://app.mysite.co.uk/login?next=/home")]
    [InlineData("mysite.com:8443/admin", "https://mysite.com:8443/admin")]
    [InlineData("localhost:3000", "https://localhost:3000")]
    [InlineData("127.0.0.1:8080", "https://127.0.0.1:8080")]
    public void A_bare_address_is_given_https(string written, string saved) =>
        Assert.Equal(saved, PluginTableColumn.WithScheme(written));

    [Theory]
    [InlineData("https://mysite.com")]
    [InlineData("https://www.mysite.com/login")]
    [InlineData("http://mysite.com")]
    [InlineData("HTTP://MySite.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://mysite.com")]
    [InlineData("mailto:a@mysite.com")]
    [InlineData("data:text/html,hi")]
    [InlineData("https://")]
    [InlineData("not a url")]
    [InlineData("foo bar.com")]
    [InlineData("/login")]
    [InlineData("//mysite.com")]
    [InlineData("dashboard")]
    [InlineData("mysite")]
    [InlineData("1.2.3")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_value_with_a_scheme_a_path_or_junk_is_left_exactly_as_written(string written) =>
        Assert.Equal(written, PluginTableColumn.WithScheme(written));

    [Fact]
    public void A_save_gives_only_the_url_columns_a_scheme_and_never_other_hosts()
    {
        var saved = Table().WithSchemes(JsonDocument.Parse("""
            [
              {"name":"a","siteUrl":"mysite.com","signInUrl":"www.mysite.com/login","hosts":"login.mysite.com, https://sso.mysite.com/x"},
              {"name":"b","siteUrl":"http://mysite.com","signInUrl":"/login","hosts":"www.mysite.com"},
              {"name":"c","siteUrl":"javascript:alert(1)","signInUrl":"not a url"}
            ]
            """).RootElement);

        var rows = saved.EnumerateArray().ToList();
        Assert.Equal("https://mysite.com", rows[0].GetProperty("siteUrl").GetString());
        Assert.Equal("https://www.mysite.com/login", rows[0].GetProperty("signInUrl").GetString());
        Assert.Equal("login.mysite.com, https://sso.mysite.com/x", rows[0].GetProperty("hosts").GetString());
        Assert.Equal("http://mysite.com", rows[1].GetProperty("siteUrl").GetString());
        Assert.Equal("/login", rows[1].GetProperty("signInUrl").GetString());
        Assert.Equal("www.mysite.com", rows[1].GetProperty("hosts").GetString());
        Assert.Equal("javascript:alert(1)", rows[2].GetProperty("siteUrl").GetString());
        Assert.Equal("not a url", rows[2].GetProperty("signInUrl").GetString());
    }

    [Fact]
    public void Rows_with_nothing_to_change_are_answered_as_sent()
    {
        var sent = JsonDocument.Parse("""[{"name":"a","siteUrl":"https://mysite.com","hosts":"mysite.com"}]""").RootElement;
        Assert.Equal(sent.GetRawText(), Table().WithSchemes(sent).GetRawText());
    }

    [Fact]
    public void The_format_is_described_and_only_url_on_a_string_column_is_taken()
    {
        var table = Table();
        Assert.Equal("url", table.Column("siteUrl")!.Format);
        Assert.Null(table.Column("hosts")!.Format);
        Assert.Contains("\"format\":\"url\"", JsonSerializer.Serialize(table.Describe()));

        Assert.Equal("`config.entries.columns.siteUrl.format` must be \"url\".", Refusal("""{ "name": "siteUrl", "format": "email" }"""));
        Assert.Equal("`config.entries.columns.n.format` applies only to a string column.", Refusal("""{ "name": "n", "type": "number", "format": "url" }"""));
    }
}
