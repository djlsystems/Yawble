using Harness.Contracts;
using Harness.Identity;
using System.Net;
var store = new SqlitePrincipalStore("/tmp/lior-w27510/dr/messages.db");
foreach (var label in new[] { "VerificationMember", "VerificationContainer" })
{
    // Members (including Manager) are authenticated as PrincipalKind.Container.
    // Mint with the production issuer; hold the generated credential only in memory.
    var key = await store.MintAsync($"DemoTeam/{label}", PrincipalKind.Container, "DemoTeam", Permits.All);
    using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:3871") };
    client.DefaultRequestHeaders.Add("X-Api-Key", key);
    var response = await client.PostAsync("/api/teams/DemoTeam/sites/team-dashboard/open", null);
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"{label}: {(int)response.StatusCode} {body}");
    if (response.StatusCode != HttpStatusCode.Forbidden || !body.Contains("That is a person's action."))
        throw new Exception("Expected person-only refusal");
}
using var anonymous = new HttpClient();
var anon = await anonymous.PostAsync("http://127.0.0.1:3871/api/teams/DemoTeam/sites/team-dashboard/open", null);
Console.WriteLine($"Anonymous: {(int)anon.StatusCode}");
if (anon.StatusCode != HttpStatusCode.Unauthorized) throw new Exception("Expected 401");
