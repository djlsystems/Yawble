using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Data.Sqlite;

namespace Harness.Tests.Host;

/// <summary>
/// ORIGIN IS DERIVED, NEVER PUBLISHED, through the real <c>tell</c> route: the row's origin comes from the
/// request's authenticated principal, handed to the log as an <see cref="AppendContext"/>, and nothing in
/// the body can set it. A person is stored as themselves, a key as its owner (still readable once the key
/// is revoked), and a member as an agent.
/// </summary>
public sealed class MessageOriginTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_persons_tell_roots_its_workflow_as_that_person_and_origin_fields_in_the_body_are_ignored()
    {
        using var person = await host.PersonAsync();

        var seq = await TellAsync(person, new
        {
            instruction = "Look at the board.",
            originKind = "trigger",
            origin_kind = "system",
            originActorId = "someone-else",
            originActorEmail = "boss@example.test",
            originTrigger = "schedule:forged",
            publisherKind = "host",
        });

        Assert.Equal(
            [OriginKinds.Person, await UserIdAsync(), "person@example.test", null, PublisherKinds.Person],
            await OriginAsync(seq));
    }

    [Fact]
    public async Task A_keys_tell_is_stored_as_its_owner_and_still_reads_after_the_key_is_revoked()
    {
        using var person = await host.PersonAsync();
        var minted = await person.PostAsJsonAsync("/api/keys", new { label = $"tool {Guid.NewGuid():N}" }, Ct);
        minted.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await minted.Content.ReadAsStringAsync(Ct)).RootElement;
        var keyId = body.GetProperty("id").GetString()!;

        long seq;
        using (var key = host.Container(body.GetProperty("credential").GetString()!))
        {
            seq = await TellAsync(key, new { instruction = "From a tool." });
        }

        (await person.DeleteAsync($"/api/keys/{keyId}", Ct)).EnsureSuccessStatusCode();

        Assert.Equal(
            [OriginKinds.Person, await UserIdAsync(), "person@example.test", null, PublisherKinds.Person],
            await OriginAsync(seq));
    }

    [Fact]
    public async Task A_members_tell_is_an_agents()
    {
        using var member = host.Container(host.AlphaContainerKey);

        var seq = await TellAsync(member, new { instruction = "From a member.", originKind = "person" });

        var worker = new ContainerId(host.Alpha, "Worker").ToString();
        Assert.Equal([OriginKinds.Agent, worker, null, null, PublisherKinds.Agent], await OriginAsync(seq));
    }

    private async Task<long> TellAsync(HttpClient client, object body)
    {
        var told = await client.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers/{TeamRegistry.DefaultManagerName}/tell", body, Ct);
        told.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await told.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("seq").GetInt64();
    }

    private Task<string?> UserIdAsync() => ScalarAsync("SELECT id FROM users WHERE email = 'person@example.test'");

    private async Task<object?[]> OriginAsync(long seq)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT origin_kind, origin_actor_id, origin_actor_email, origin_trigger, publisher_kind FROM messages WHERE seq = $seq";
        command.Parameters.AddWithValue("$seq", seq);

        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return [.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i))];
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(host.DataRoot, "messages.db"),
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
