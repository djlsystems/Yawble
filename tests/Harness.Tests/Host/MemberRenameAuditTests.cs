using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A MEMBER SAVE THAT CHANGES NOTHING WRITES NOTHING. The Member settings dialog sends the name on
/// every save; a "rename" to the name the member already has is not a rename, and a save that
/// changes nothing appends no `member.changed` row.
/// </summary>
public sealed class MemberRenameAuditTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<IReadOnlyList<JsonElement>> MemberChangedAsync(string member) =>
        [.. (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(take: 10_000, ct: Ct)).Events
            .Where(e => e.Action == TenantActions.MemberChanged && e.Subject == $"{host.Alpha}/{member}")
            .OrderBy(e => e.Seq)
            .Select(e => JsonDocument.Parse(e.Detail!).RootElement.Clone())];

    [Fact]
    public async Task A_rename_to_the_same_name_is_ignored_and_a_save_that_changes_nothing_writes_no_row()
    {
        using var client = await host.PersonAsync();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;
        var hired = await client.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers", new { name = "Scribe", agent }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);

        // The same name, nothing else: nothing changed, nothing written.
        var same = await client.PatchAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers/Scribe", new { name = "Scribe" }, Ct);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Empty(await MemberChangedAsync("Scribe"));

        // The same name with new words: a change, and not a rename.
        var told = await client.PatchAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers/Scribe", new { name = "Scribe", systemPrompt = "Be brief." }, Ct);
        Assert.Equal(HttpStatusCode.OK, told.StatusCode);
        var change = Assert.Single(await MemberChangedAsync("Scribe"));
        Assert.False(change.GetProperty("renamed").GetBoolean());
        Assert.True(change.GetProperty("promptChanged").GetBoolean());

        // Another name is a rename.
        var renamed = await client.PatchAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers/Scribe", new { name = "Quill" }, Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.True((await MemberChangedAsync("Scribe"))[^1].GetProperty("renamed").GetBoolean());
    }
}
