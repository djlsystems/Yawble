using System.Text.RegularExpressions;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE ADMIN-APPROVAL SENTENCE IS ONE SENTENCE IN TWO PLACES. The Host answers a refused Microsoft
/// sign-in with <see cref="ConnectionProviders.MicrosoftAdminApproval"/>, and the web shows
/// <c>AdminApprovalSentence</c> from <c>web/src/lib/connections.ts</c> for the same refusal; a person
/// must read the same words whichever one they meet. The web constant is read from its source file.
/// </summary>
public sealed class AdminApprovalSentenceTests
{
    private static readonly string WebFile = Path.Combine(SolutionSamples.RepoRoot(), "web", "src", "lib", "connections.ts");

    [Fact]
    public void The_web_sentence_is_the_Hosts_word_for_word()
    {
        var web = WebSentence(File.ReadAllText(WebFile));
        var host = ConnectionProviders.MicrosoftAdminApproval;

        Assert.True(
            string.Equals(host, web, StringComparison.Ordinal),
            $"The Host's ConnectionProviders.MicrosoftAdminApproval is \"{host}\" but the web's AdminApprovalSentence ({WebFile}) is \"{web}\".");
    }

    /// <summary>The value of <c>export const AdminApprovalSentence</c>, a single- or double-quoted
    /// string literal with no escapes.</summary>
    private static string WebSentence(string source)
    {
        var match = Regex.Match(source, """export\s+const\s+AdminApprovalSentence\s*=\s*(?<quote>['"])(?<value>[^'"\\]*)\k<quote>""");
        Assert.True(match.Success, $"No `export const AdminApprovalSentence = '...'` literal in {WebFile}.");
        return match.Groups["value"].Value;
    }
}
