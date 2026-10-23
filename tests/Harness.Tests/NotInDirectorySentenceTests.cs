using System.Text.RegularExpressions;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE NOT-IN-THE-DIRECTORY SENTENCE IS ONE SENTENCE IN TWO PLACES. The Host answers a Microsoft sign-in
/// refused because the account is not in the directory it used with
/// <see cref="ConnectionProviders.MicrosoftNotInDirectory"/>, and the web shows <c>NotInDirectorySentence</c>
/// from <c>web/src/lib/connections.ts</c> for the same refusal; the line a Microsoft code that ran out adds
/// (<see cref="ConnectionProviders.MicrosoftExpiredNotInDirectory"/>) is <c>ExpiredNotInDirectoryLine</c>
/// there. A person reads the same words whichever one they meet. The web constants are read from their
/// source file.
/// </summary>
public sealed class NotInDirectorySentenceTests
{
    private static readonly string WebFile = Path.Combine(SolutionSamples.RepoRoot(), "web", "src", "lib", "connections.ts");

    [Fact]
    public void The_web_sentence_is_the_Hosts_word_for_word()
    {
        var web = WebConstant(File.ReadAllText(WebFile), "NotInDirectorySentence");
        var host = ConnectionProviders.MicrosoftNotInDirectory;

        Assert.True(
            string.Equals(host, web, StringComparison.Ordinal),
            $"The Host's ConnectionProviders.MicrosoftNotInDirectory is \"{host}\" but the web's NotInDirectorySentence ({WebFile}) is \"{web}\".");
    }

    [Fact]
    public void The_web_expiry_line_is_the_Hosts_word_for_word()
    {
        var web = WebConstant(File.ReadAllText(WebFile), "ExpiredNotInDirectoryLine");
        var host = ConnectionProviders.MicrosoftExpiredNotInDirectory;

        Assert.True(
            string.Equals(host, web, StringComparison.Ordinal),
            $"The Host's ConnectionProviders.MicrosoftExpiredNotInDirectory is \"{host}\" but the web's ExpiredNotInDirectoryLine ({WebFile}) is \"{web}\".");
    }

    [Fact]
    public void The_sentence_names_no_code_and_says_where_to_name_the_directory()
    {
        var sentence = ConnectionProviders.MicrosoftNotInDirectory;

        Assert.DoesNotContain("AADSTS", sentence);
        Assert.Contains("not found in the directory this sign-in used", sentence);
        Assert.Contains("personal Microsoft account that owns an Azure subscription", sentence);
        Assert.Contains("under Directory and sign in again", sentence);
        Assert.Contains("a work account in that directory works too", sentence);
        Assert.Single(sentence.Split(". ", StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void The_microsoft_guide_says_before_signing_in_where_an_account_not_in_the_directory_is_named()
    {
        var microsoft = ConnectionProviders.Resolve(ConnectionProviders.Microsoft, null, "5d1f7e20-3c4b-4a8e-9f06-1b2c3d4e5f60");
        var steps = ConnectionGuides.For(microsoft, ["https://graph.microsoft.com/Mail.Read"], "https://host.example.test")!;
        var credentials = steps.Single(s => s.Id == "credentials");

        Assert.Contains("If Microsoft says the username may be incorrect, the account is not in the directory the sign-in used", credentials.Text);
        Assert.Contains("on the Sign in step", credentials.Text);

        var web = WebConstant(File.ReadAllText(WebFile), "UsernameIncorrectHint");
        Assert.StartsWith("If Microsoft says the username may be incorrect, the account is not in the directory the sign-in used", web);
    }

    /// <summary>The value of <c>export const <paramref name="name"/></c>, a single- or double-quoted string
    /// literal with no escapes; the other quote may appear inside it.</summary>
    private static string WebConstant(string source, string name)
    {
        var match = Regex.Match(source, $$"""export\s+const\s+{{name}}\s*=\s*(?<quote>['"])(?<value>(?:(?!\k<quote>)[^\\])*)\k<quote>""");
        Assert.True(match.Success, $"No `export const {name} = '...'` literal in {WebFile}.");
        return match.Groups["value"].Value;
    }
}
