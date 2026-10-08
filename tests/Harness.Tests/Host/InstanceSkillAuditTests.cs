using System.Net;
using System.Net.Http.Json;
using Harness.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// AN INSTANCE-WIDE SKILL IS NAMED ON ITS TENANT ROWS. Created, changed and deleted through
/// <c>/api/skills</c>, each row carries the skill's name as its subject, so the Admin Log's To column
/// says which skill - as a team skill's rows do - rather than standing empty.
/// </summary>
public sealed class InstanceSkillAuditTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Skill(string name) =>
        new { name, description = $"Use when running {name}.", roles = new[] { "member" }, body = "Steps." };

    [Fact]
    public async Task An_instance_skill_created_changed_and_deleted_is_named_on_each_tenant_row()
    {
        using var person = await host.PersonAsync();
        var log = host.Services.GetRequiredService<ITenantLog>();

        Assert.Equal(HttpStatusCode.Created, (await person.PostAsJsonAsync("/api/skills/audit-first", Skill("audit-first"), Ct)).StatusCode);
        var created = await log.FindLatestAsync(TenantActions.SkillCreated, "audit-first", Ct);
        Assert.NotNull(created);
        Assert.Equal("audit-first", created.SubjectName);

        Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync("/api/skills/audit-first", Skill("audit-second"), Ct)).StatusCode);
        var changed = await log.FindLatestAsync(TenantActions.SkillChanged, "audit-second", Ct);
        Assert.NotNull(changed);
        Assert.Equal("audit-second", changed.SubjectName);

        Assert.Equal(HttpStatusCode.NoContent, (await person.DeleteAsync("/api/skills/audit-second", Ct)).StatusCode);
        var deleted = await log.FindLatestAsync(TenantActions.SkillDeleted, "audit-second", Ct);
        Assert.NotNull(deleted);
        Assert.Equal("audit-second", deleted.SubjectName);
    }
}
