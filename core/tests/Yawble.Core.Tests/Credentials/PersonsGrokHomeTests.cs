using Yawble.Core.Bus;
using Yawble.Core.Credentials;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Credentials;

/// <summary>
/// Grok's sign-in held by the gateway: the gateway reads the person's OWN Grok home
/// and the keeper has the person's own grok renew it. The app never owns, widens or deletes anything there.
/// </summary>
public class PersonsGrokHomeTests
{
    [Fact]
    public void ThePersonsGrokHomeIsGrokHomeOrDotGrok_AndIsMarkedTheirs()
    {
        var fromVar = GrokHome.OfThisComputer(v => v == "GROK_HOME" ? @"D:\grok-elsewhere" : null);
        Assert.Equal(@"D:\grok-elsewhere", fromVar.Path);
        Assert.True(fromVar.PersonsOwn);
        var dflt = GrokHome.OfThisComputer(_ => null);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok"), dflt.Path);
        Assert.False(GrokHome.InDataDir(@"C:\app\data").PersonsOwn);
    }

    [Fact]
    public void TheKeeperRunsThePersonsGrokWithTheirOwnProfile_OutsideTheirGrokHome()
    {
        using var dir = new TempDb();
        var own = GrokHome.OfThisComputer(v => v == "GROK_HOME" ? Path.Combine(dir.Dir, ".grok") : null);
        var keeper = new GrokKeeper(own, new GrokKeeperOptions { GrokBinary = "grok" });
        var psi = keeper.StartInfo(["models"]);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(own.Path, psi.Environment["GROK_HOME"]);
        Assert.Equal(profile, psi.Environment["HOME"]);
        if (OperatingSystem.IsWindows()) Assert.Equal(profile, psi.Environment["USERPROFILE"]);
        Assert.NotEqual(own.Path, psi.WorkingDirectory);
        Assert.False(psi.Environment.ContainsKey("XAI_API_KEY"));

        // The app-private home keeps its old form: it is HOME too.
        var priv = new GrokHome(Path.Combine(dir.Dir, "grok-home"));
        Assert.Equal(priv.Path, new GrokKeeper(priv, new GrokKeeperOptions { GrokBinary = "grok" }).StartInfo(["models"]).Environment["HOME"]);
    }

    [Fact]
    public void SettingsCanNeverSignThePersonOutOfTheirOwnGrok()
    {
        using var dir = new TempDb();
        var own = GrokHome.OfThisComputer(v => v == "GROK_HOME" ? Path.Combine(dir.Dir, ".grok") : null);
        Directory.CreateDirectory(own.Path);
        File.WriteAllText(own.AuthFile, "{}");
        var service = CredentialService.ForThisMachine(Path.Combine(dir.Dir, "credentials"), dir.ConnectionString, null);
        var refusal = Assert.Throws<BusRefusal>(() => new ModelCredentials(service, own).Remove("grokLogin"));
        Assert.Equal("not_app_owned", refusal.Code);
        Assert.True(File.Exists(own.AuthFile));
    }

    [Fact]
    public void GrokRunsUseTheGatewayHeldSignIn_WhichTheLauncherChecksAndNeverGrants()
    {
        Assert.Equal(CredentialMode.GrokGatewaySignIn, AgentPresets.HostSignInMode(AgentCli.Grok));
        Assert.True(AgentPresets.NeedsHostSignIn(CredentialMode.GrokGatewaySignIn));
        Assert.False(AgentPresets.IsHostSignIn(CredentialMode.GrokGatewaySignIn));
        Assert.Equal(AgentCli.Grok, AgentPresets.CliOf(CredentialMode.GrokGatewaySignIn));
    }
}
