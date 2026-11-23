using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Policy;

namespace Yawble.Core.Agents;

/// <summary>
/// What the Agents dialog sees (agents.detect): the detection, with Grok's sign-in turned to not signed in while the
/// gateway's latest Grok request was refused as a credential problem (B128). Only the window's view gets this: the
/// launcher keeps the plain detection, so a refusal never stops a run that could clear it once the person signs in again.
/// The shell reads it in the fields it already has: signIn.status ("not-signed-in") and signIn.detail (starting with
/// <see cref="SignInCatalog.GrokNotSignedIn"/>).
/// </summary>
public sealed class GatewaySignInOverlay(ICliDetector inner, GrokSignInWatch? grok, Func<string, string?>? readFile = null) : ICliDetector
{
    private readonly Func<string, string?> _read = readFile ?? ReadFile;

    public async Task<IReadOnlyList<DetectedCli>> DetectAllAsync(CancellationToken ct) =>
        [.. (await inner.DetectAllAsync(ct)).Select(Apply)];

    public async Task<DetectedCli?> DetectOneAsync(string cliId, CancellationToken ct) =>
        await inner.DetectOneAsync(cliId, ct) is { } found ? Apply(found) : null;

    private DetectedCli Apply(DetectedCli cli)
    {
        if (grok is null || !string.Equals(cli.CliId, AgentClis.Grok, StringComparison.OrdinalIgnoreCase)) return cli;
        if (cli.SignIn is not { Status: SignInStatus.SignedIn } signIn || signIn.Files.FirstOrDefault() is not { } file) return cli;
        var refusal = grok.Current(GrokHome.Parse(_read(file.Path)));
        return refusal is null ? cli : cli with { SignIn = SignInCatalog.GrokRefused(signIn, refusal) };
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
