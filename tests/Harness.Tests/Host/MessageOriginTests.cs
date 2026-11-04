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

    /// <summary>
    /// A PERSON'S INSTRUCTION AND WHAT FOLLOWS FROM IT, AT ANY DEPTH, through the real route: a member's tell caused by
    /// the person's, and one caused by that, carry the person as origin - while each row's publisher is the agent that
    /// wrote it.
    /// </summary>
    [Fact]
    public async Task Rows_a_member_writes_under_a_persons_instruction_carry_that_person_at_every_depth()
    {
        using var person = await host.PersonAsync();
        using var member = host.Container(host.AlphaContainerKey);
        var root = await TellAsync(person, new { instruction = "Start the work." });
        var child = await TellAsync(member, new { instruction = "Next step.", causation = root.ToString(), originKind = "agent" });
        var grandchild = await TellAsync(member, new { instruction = "And the one after.", causation = child.ToString() });

        var userId = await UserIdAsync();
        Assert.Equal([OriginKinds.Person, userId, "person@example.test", null, PublisherKinds.Person], await OriginAsync(root));
        foreach (var seq in new[] { child, grandchild })
        {
            Assert.Equal([OriginKinds.Person, userId, "person@example.test", null, PublisherKinds.Agent], await OriginAsync(seq));
        }

        Assert.Equal("2", await ScalarAsync($"SELECT CAST(depth - (SELECT depth FROM messages WHERE seq = {root}) AS TEXT) FROM messages WHERE seq = {grandchild}"));
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

/// <summary>
/// NO PUBLISHER CAN CLAIM AN ORIGIN, by the shape of the public append: <see cref="NewMessage"/> - all a publisher
/// hands the log - has no field that could carry one, and the only place an <see cref="AppendContext"/> is made is
/// <c>AppendContexts.From</c>, out of the request's own authenticated principal.
/// </summary>
public sealed class MessageOriginShapeTests
{
    [Fact]
    public void A_new_message_has_no_origin_field_and_an_append_context_is_made_only_from_the_requests_principal()
    {
        Assert.Equal(
            ["CausationSeq", "Payload", "Source", "Type"],
            typeof(NewMessage).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["CausationSeq", "Payload", "Source", "Type"],
            typeof(NewMessage).GetConstructors().Single().GetParameters().Select(p => p.Name!).Order(StringComparer.Ordinal));

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Harness.slnx"))) root = root.Parent;
        Assert.NotNull(root);

        var made = Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"new\s+AppendContext\s*\(|new\s*\(\s*PrincipalKind\."))
            .Select(f => Path.GetRelativePath(root.FullName, f).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();
        Assert.Equal(["src/Harness.Host/Auth/AppendContexts.cs"], made);
    }
}
