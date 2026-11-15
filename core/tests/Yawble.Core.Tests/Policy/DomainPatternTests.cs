using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Yawble.Core.Policy;
using Yawble.Core.Sandboxing;
using Yawble.Core.Storage;
using Xunit;

namespace Yawble.Core.Tests.Policy;

/// <summary>B018 (card 183): the bundled Public Suffix List and the PublicSuffix lookups.</summary>
public class PublicSuffixTests
{
    [Fact]
    public void TheListIsThePinnedEmbeddedCopy_WithBothSections()
    {
        using var stream = typeof(PublicSuffix).Assembly.GetManifestResourceStream(PublicSuffix.ResourceName);
        Assert.NotNull(stream);
        // The pinned bytes (docs/architecture.md names this hash); a new copy is a reviewed change, never a download.
        Assert.Equal("8576b335b871dea99dc0fccd04138311bd43f7a8da256c31bf964f11e2f7c179", Convert.ToHexStringLower(SHA256.HashData(stream)));
        Assert.Equal("2026-11-13_14-44-35_UTC", PublicSuffix.Version);
        Assert.Equal("71c235dd3f888dd4ecd066817050a7482da710e5", PublicSuffix.Commit);
    }

    [Theory]
    [InlineData("crl3.digicert.com", "digicert.com")]
    [InlineData("digicert.com", "digicert.com")]
    [InlineData("CRL3.DigiCert.com", "digicert.com")]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    [InlineData("a.b.bbc.co.uk", "bbc.co.uk")]
    [InlineData("api.nuget.org", "nuget.org")]
    [InlineData("pat.github.io", "pat.github.io")] // PRIVATE section: github.io is the suffix
    [InlineData("x.pat.github.io", "pat.github.io")]
    [InlineData("app.herokuapp.com", "app.herokuapp.com")]
    [InlineData("d111.cloudfront.net", "d111.cloudfront.net")]
    [InlineData("site.azurewebsites.net", "site.azurewebsites.net")]
    [InlineData("tunnel.ngrok.io", "tunnel.ngrok.io")]
    [InlineData("bucket.s3.amazonaws.com", "bucket.s3.amazonaws.com")]
    [InlineData("ec2.amazonaws.com", "amazonaws.com")] // amazonaws.com itself is not listed; its tenants' suffixes are
    [InlineData("a.b.c.compute.amazonaws.com", "b.c.compute.amazonaws.com")] // *.compute.amazonaws.com
    [InlineData("foo.bar.ck", "foo.bar.ck")] // ICANN wildcard *.ck
    [InlineData("www.ck", "www.ck")] // exception !www.ck: registrable, its suffix is ck
    [InlineData("a.www.ck", "www.ck")]
    [InlineData("host.example.unlistedtld", "example.unlistedtld")] // the implicit "*" rule
    public void RegistrableDomain_IsETldPlusOne(string host, string expected) =>
        Assert.Equal(expected, PublicSuffix.RegistrableDomain(host));

    [Theory]
    [InlineData("com")] // bare TLD
    [InlineData("co.uk")] // ICANN suffix
    [InlineData("github.io")] // PRIVATE suffix
    [InlineData("herokuapp.com")]
    [InlineData("s3.amazonaws.com")]
    [InlineData("bar.ck")] // *.ck makes bar.ck a suffix
    [InlineData("x.compute.amazonaws.com")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    [InlineData("1.2.3")]
    [InlineData("xn--pypi-9qa.org")] // IDN in xn-- form
    [InlineData("pypí.org")] // IDN in Unicode
    [InlineData("api.xn--fiqs8s")] // an xn-- TLD
    [InlineData("")]
    [InlineData("example.com.")]
    [InlineData(".example.com")]
    [InlineData("a..example.com")]
    [InlineData("*.example.com")]
    [InlineData("ex_ample.com")]
    [InlineData("-bad.example.com")]
    [InlineData("api.nuget.org:443")]
    public void RegistrableDomain_IsNull_ForASuffix_AnAddress_AnIdn_OrAMalformedName(string host) =>
        Assert.Null(PublicSuffix.RegistrableDomain(host));

    [Theory]
    [InlineData("crl3.digicert.com", "*.digicert.com")]
    [InlineData("ocsp.digicert.com", "*.digicert.com")]
    [InlineData("digicert.com", "*.digicert.com")]
    [InlineData("www.bbc.co.uk", "*.bbc.co.uk")]
    [InlineData("api.nuget.org", "*.nuget.org")]
    [InlineData("dl.google.com", "*.google.com")]
    public void DomainPatternFor_OffersTheRegistrableDomain(string host, string expected) =>
        Assert.Equal(expected, PublicSuffix.DomainPatternFor(host));

    [Theory]
    [InlineData("pat.github.io")] // under a multi-tenant PRIVATE suffix
    [InlineData("x.pat.github.io")]
    [InlineData("app.herokuapp.com")]
    [InlineData("d111.cloudfront.net")]
    [InlineData("site.azurewebsites.net")]
    [InlineData("tunnel.ngrok.io")]
    [InlineData("bucket.s3.amazonaws.com")]
    [InlineData("ec2.amazonaws.com")] // amazonaws.com has tenants' suffixes under it
    [InlineData("storage.googleapis.com")] // googleapis.com is a PRIVATE suffix
    [InlineData("objects.githubusercontent.com")] // so is githubusercontent.com
    [InlineData("login.windows.net")] // blob.core.windows.net and others sit under windows.net
    [InlineData("github.io")] // a suffix itself
    [InlineData("co.uk")]
    [InlineData("com")]
    [InlineData("127.0.0.1")]
    [InlineData("xn--pypi-9qa.org")]
    [InlineData("api.anthropic.com")] // a vendor domain (floor rule)
    [InlineData("console.anthropic.com")]
    [InlineData("eu.api.x.ai")]
    [InlineData("bus.yawble.internal")] // the gateway's virtual hosts
    [InlineData("api.githubcopilot.com")] // a preset-only vendor domain
    [InlineData("auth.openai.com")]
    [InlineData("ab.chatgpt.com")]
    public void DomainPatternFor_IsNull_WhenNoDomainAllowMayBeOffered(string host) =>
        Assert.Null(PublicSuffix.DomainPatternFor(host));

    [Fact]
    public void TheMultiTenantSuffixesTheSpecNames_AreInThePrivateSection()
    {
        foreach (var suffix in new[] { "github.io", "cloudfront.net", "azurewebsites.net", "herokuapp.com", "ngrok.io", "s3.amazonaws.com", "googleapis.com" })
        {
            var m = PublicSuffix.Match("tenant." + suffix);
            Assert.NotNull(m);
            Assert.Equal(suffix, m.Suffix);
            Assert.True(m.SuffixIsPrivate, suffix);
        }
        // co.uk is ICANN; an unlisted TLD falls to the implicit rule, never PRIVATE.
        Assert.False(PublicSuffix.Match("bbc.co.uk")!.SuffixIsPrivate);
        Assert.False(PublicSuffix.Match("example.unlistedtld")!.SuffixIsPrivate);
    }

    [Fact]
    public void Parse_ReadsSections_WildcardsAndExceptions_AndSkipsRulesOutsideTheSections()
    {
        const string list = """
            // VERSION: test
            // COMMIT: abc
            stray.example
            // ===BEGIN ICANN DOMAINS===
            uk
            co.uk
            *.ck
            !www.ck
            // ===END ICANN DOMAINS===
            // ===BEGIN PRIVATE DOMAINS===
            s3.amazonaws.com
            // ===END PRIVATE DOMAINS===
            """;
        var rules = PublicSuffix.Parse(new StringReader(list));
        Assert.Equal(("test", "abc"), (rules.Version, rules.Commit));
        Assert.False(rules.Normal.ContainsKey("stray.example"));
        Assert.False(rules.Normal["co.uk"]);
        Assert.True(rules.Normal["s3.amazonaws.com"]);
        Assert.True(rules.Wildcards.ContainsKey("ck"));
        Assert.True(rules.Exceptions.ContainsKey("www.ck"));
        Assert.True(rules.HasSuffixUnder("amazonaws.com"));
        Assert.True(rules.HasSuffixUnder("ck"));
        Assert.True(rules.HasSuffixUnder("uk"));
        Assert.False(rules.HasSuffixUnder("co.uk"));
    }
}

/// <summary>B018 (card 183): FloorRules.HostProblem for domain allows ("*.domain").</summary>
public class FloorRulesDomainPatternTests
{
    [Theory]
    [InlineData("*.digicert.com")]
    [InlineData("*.bbc.co.uk")]
    [InlineData("*.nuget.org")]
    [InlineData("*.pypi.org")]
    [InlineData("*.github.com")] // api.github.com is a Copilot preset host, but github.com is not preset-only
    [InlineData("*.example.unlistedtld")]
    public void ADomainAllowOverARegistrableDomain_IsAccepted(string pattern) =>
        Assert.Null(FloorRules.HostProblem(pattern));

    [Theory]
    [InlineData("*", "wildcard")]
    [InlineData("*.", "valid DNS")]
    [InlineData("**.example.com", "wildcard")]
    [InlineData("*.*.example.com", "wildcard")]
    [InlineData("a.*.b.com", "wildcard")]
    [InlineData("*example.com", "wildcard")]
    [InlineData("example.*", "wildcard")]
    [InlineData("*.com", "public suffix")]
    [InlineData("*.co.uk", "public suffix")]
    [InlineData("*.uk", "public suffix")]
    [InlineData("*.github.io", "public suffix")]
    [InlineData("*.s3.amazonaws.com", "public suffix")]
    [InlineData("*.pat.github.io", "shared suffix")]
    [InlineData("*.app.herokuapp.com", "shared suffix")]
    [InlineData("*.d111.cloudfront.net", "shared suffix")]
    [InlineData("*.amazonaws.com", "other public suffixes")]
    [InlineData("*.sub.digicert.com", "registrable domain only")]
    [InlineData("*.anthropic.com", "model vendor")]
    [InlineData("*.claude.ai", "model vendor")]
    [InlineData("*.api.x.ai", "model vendor")]
    [InlineData("*.grok.com", "model vendor")]
    [InlineData("*.yawble.internal", "gateway virtual host")]
    [InlineData("*.githubcopilot.com", "CLI preset")]
    [InlineData("*.openai.com", "CLI preset")]
    [InlineData("*.chatgpt.com", "CLI preset")]
    [InlineData("*.oaiusercontent.com", "CLI preset")]
    [InlineData("*.xn--pypi-9qa.org", "xn--")]
    [InlineData("*.pypí.org", "xn--")]
    [InlineData("*.127.0.0.1", "")]
    [InlineData("*.Example.com", "lower-case")]
    [InlineData(" *.example.com", "lower-case")]
    public void OtherPatterns_AreRefused(string pattern, string reason)
    {
        var problem = FloorRules.HostProblem(pattern);
        Assert.NotNull(problem);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactHosts_KeepTheirRules()
    {
        Assert.Null(FloorRules.HostProblem("crl3.digicert.com"));
        Assert.Null(FloorRules.HostProblem("digicert.com"));
        Assert.Null(FloorRules.HostProblem("pat.github.io")); // an exact host under a shared suffix is fine
        Assert.NotNull(FloorRules.HostProblem("api.anthropic.com"));
        Assert.NotNull(FloorRules.HostProblem("bus.yawble.internal"));
        Assert.NotNull(FloorRules.HostProblem("10.0.0.1"));
    }

    [Fact]
    public void PublicSuffixAndFloorRulesAgree_APatternIsOfferedOnlyIfAccepted()
    {
        foreach (var host in new[] { "crl3.digicert.com", "pat.github.io", "ec2.amazonaws.com", "api.githubcopilot.com", "api.anthropic.com", "www.bbc.co.uk" })
        {
            var pattern = PublicSuffix.DomainPatternFor(host);
            var registrable = PublicSuffix.RegistrableDomain(host);
            if (pattern is not null) Assert.Null(FloorRules.HostProblem(pattern));
            else if (registrable is not null) Assert.NotNull(FloorRules.HostProblem("*." + registrable));
        }
    }
}

/// <summary>B018 (card 183): the policy editors' backend takes "*.domain" in the loosen block, the team list and the global hosts.</summary>
public sealed class PolicyDomainPatternTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("yawble-policy-pattern-").FullName;
    private readonly string cs;

    public PolicyDomainPatternTests()
    {
        cs = $"Data Source={Path.Combine(dir, "t.db")};Pooling=False";
        SchemaMigrator.Migrate(cs);
    }

    public void Dispose() => Directory.Delete(dir, true);

    private static RunLayout Linux => PolicyFixtures.Layout(TargetOs.Linux, AgentCli.Claude);

    private static PolicyPatch L(string json) => PolicyPatch.Parse(json, PatchKind.Loosen);

    [Fact]
    public void TheGlobalHosts_TakeADomainAllow_AndRefuseABadOne()
    {
        var store = new PolicyStore(cs);
        store.SaveGlobal(PolicyFixtures.Global with { Network = new NetworkPolicy(["registry.npmjs.org", "*.digicert.com"]) }, Linux);
        Assert.Equal(["registry.npmjs.org", "*.digicert.com"], store.GetGlobal().Network.AllowHosts);
        // Not the apex: only what is listed.
        Assert.DoesNotContain("digicert.com", store.Effective(7, Linux).Policy.AllowHosts);

        foreach (var bad in new[] { "*.com", "*.github.io", "*.anthropic.com", "a.*.b.com" })
        {
            var e = Assert.Throws<PolicyException>(() => store.SaveGlobal(PolicyFixtures.Global with { Network = new NetworkPolicy([bad]) }, Linux));
            Assert.Contains(e.Errors, x => x.StartsWith("global network.allowHosts:", StringComparison.Ordinal) && x.Contains(bad, StringComparison.Ordinal));
        }
        Assert.Empty(PolicyMerger.CheckGlobal(PolicyFixtures.Global with { Network = new NetworkPolicy(["*.digicert.com"]) }));
    }

    [Fact]
    public void TheLoosenBlock_TakesADomainAllow_AndRefusesABadOne()
    {
        var store = new PolicyStore(cs);
        store.SaveGlobal(PolicyFixtures.Global);
        var loosen = L("""{"network":{"allowHosts":["*.digicert.com"]}}""");
        var preview = store.PreviewLoosen(7, loosen, Linux);
        Assert.Equal(["network.allowHosts + *.digicert.com"], preview);
        store.SaveLoosen(7, loosen, Linux, preview);
        var hosts = store.Effective(7, Linux).Policy.AllowHosts;
        Assert.Contains("*.digicert.com", hosts);
        Assert.DoesNotContain("digicert.com", hosts);

        foreach (var bad in new[] { "*.co.uk", "*.pat.github.io", "*.githubcopilot.com", "*" })
        {
            var e = Assert.Throws<PolicyException>(() => store.SaveLoosen(8, new PolicyPatch(Network: new NetworkPatch([bad])), Linux, []));
            Assert.Contains(e.Errors, x => x.StartsWith("network.allowHosts:", StringComparison.Ordinal));
        }
        Assert.True(store.GetMember(8).Loosen.IsEmpty);
    }

    [Fact]
    public void TheMerger_AddsTheTeamsDomainAllow_AndDropsARefusedOneWithANote()
    {
        var r = PolicyMerger.Merge(PolicyFixtures.Global, null, null, Linux, teamHosts: ["*.digicert.com", "*.github.io"]);
        Assert.Contains("*.digicert.com", r.Policy.AllowHosts);
        Assert.DoesNotContain("*.github.io", r.Policy.AllowHosts);
        Assert.DoesNotContain("digicert.com", r.Policy.AllowHosts);
        Assert.Contains(r.Policy.Notes, n => n.Contains("*.github.io", StringComparison.Ordinal));
        Assert.Empty(r.Violations);
    }

    [Fact]
    public void AddTeamHost_TakesADomainAllow_AndRefusesABadOne_NothingWritten()
    {
        Exec("INSERT INTO teams (id, name, created_at) VALUES (5, 'payments', 't');" +
             "INSERT INTO members (id, team_id, name, role, created_at) VALUES (51, 5, 'Ada', 'member', 't');");
        var store = new PolicyStore(cs, (_, _, _, _) => []);

        Assert.True(store.AddTeamHost(5, "*.digicert.com", [(51, Linux)], "pat", "app"));
        Assert.Equal("*.digicert.com", Assert.Single(store.TeamHosts(5)).Host);
        var hosts = store.Effective(51, Linux).Policy.AllowHosts;
        Assert.Contains("*.digicert.com", hosts);
        Assert.DoesNotContain("digicert.com", hosts);

        foreach (var bad in new[] { "*.com", "*.co.uk", "*.github.io", "*.amazonaws.com", "*.anthropic.com", "*.yawble.ai", "*.corp", "*.example.com.", "**.example.com" })
            Assert.Throws<PolicyException>(() => store.AddTeamHost(5, bad, [(51, Linux)], "pat", "app"));
        Assert.Single(store.TeamHosts(5));
    }

    private void Exec(string sql)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
