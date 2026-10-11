using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Host;

namespace Harness.Tests.Host;

/// <summary>
/// The Host is stamped from <c>scripts/version.mjs</c>, and an anonymous
/// <c>GET /api/version</c> says which build it is. How the string is formed from a tag is pinned on
/// the web side, in <c>version-script.spec.ts</c>, against a real throwaway repository.
/// </summary>
public sealed partial class VersionEndpointTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static readonly Assembly HostAssembly = typeof(BuildVersion).Assembly;

    /// <summary>Every shape version.mjs prints, and 0.0.0+unknown for a build that could not ask.</summary>
    [GeneratedRegex(@"^(\d{4}\.\d{2}\.\d{2}\.[1-9]\d*|0\.0\.0)(\+\d+\.[0-9a-f]{7,}(\.dirty)?)?$|^0\.0\.0\+unknown$")]
    private static partial Regex VersionShape();

    [Fact]
    public async Task Version_is_anonymous_and_answers_the_hosts_own_stamp()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = host.Anonymous();

        var response = await client.GetAsync("/api/version", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = body.RootElement;

        var version = root.GetProperty("version").GetString()!;
        Assert.Equal(
            HostAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            version);
        Assert.Matches(VersionShape(), version);

        Assert.Matches("^([0-9a-f]{40}|unknown)$", root.GetProperty("commit").GetString()!);

        // Stamped at compile time, so it is in the past and not by much: this test run built it.
        var builtAt = root.GetProperty("builtAt").GetDateTimeOffset();
        Assert.InRange(builtAt, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void The_numeric_assembly_version_is_the_release_without_padding()
    {
        var informational = HostAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var release = informational.Split('+')[0];
        var expected = Version.Parse(release);

        // Version.Parse reads "09" as 9, which is the unpadded number the fields must hold.
        Assert.Equal(expected.ToString(), string.Join('.', release.Split('.').Select(int.Parse)));
        Assert.Equal(Normalise(expected), HostAssembly.GetName().Version);
        Assert.Equal(
            Normalise(expected).ToString(),
            HostAssembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
    }

    [Fact]
    public void A_release_is_read_back_from_the_attributes_the_build_writes()
    {
        var stamped = BuildVersion.Of(new StampedAssembly(
            new AssemblyInformationalVersionAttribute("2026.09.23.1"),
            new AssemblyMetadataAttribute("HarnessCommit", "eed3fd00b9717e141ce8e505e99dbd76e3586035"),
            new AssemblyMetadataAttribute("HarnessBuiltAt", "2026-09-23T12:00:00Z")));

        Assert.Equal(
            new BuildVersion(
                "2026.09.23.1",
                "eed3fd00b9717e141ce8e505e99dbd76e3586035",
                DateTimeOffset.Parse("2026-09-23T12:00:00Z")),
            stamped);
    }

    [Fact]
    public void An_unstamped_assembly_says_unknown_rather_than_looking_like_a_release()
    {
        Assert.Equal(new BuildVersion("0.0.0+unknown", "unknown", null), BuildVersion.Of(new StampedAssembly()));
    }

    [Fact]
    public void No_build_file_holds_the_version_as_a_literal()
    {
        var root = RepoRoot();
        var literal = new Regex(@"<(Version|AssemblyVersion|FileVersion|InformationalVersion)>\s*\d");

        var hits = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".props") || f.EndsWith(".csproj") || f.EndsWith(".targets"))
            .Where(f => !f.Replace('\\', '/').Contains("/obj/") && !f.Replace('\\', '/').Contains("/bin/"))
            .Where(f => literal.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.Empty(hits);
    }

    private static Version Normalise(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No Harness.slnx above the test output.");
    }

    /// <summary>An assembly carrying exactly the attributes it is given.</summary>
    private sealed class StampedAssembly(params Attribute[] attributes) : Assembly
    {
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) =>
            ToArray(attributes.Where(attributeType.IsInstanceOfType), attributeType);

        public override object[] GetCustomAttributes(bool inherit) => ToArray(attributes, typeof(Attribute));

        private static object[] ToArray(IEnumerable<Attribute> source, Type type)
        {
            var list = source.ToList();
            var array = Array.CreateInstance(type, list.Count);
            for (var i = 0; i < list.Count; i++) array.SetValue(list[i], i);
            return (object[])array;
        }
    }
}
