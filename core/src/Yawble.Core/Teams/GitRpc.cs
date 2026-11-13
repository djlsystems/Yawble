using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;

namespace Yawble.Core.Teams;

/// <summary>
/// git.check (the git found on this computer) and git.install (installs or updates Git for Windows; the person, in the
/// app's window on this computer, only: never a cloud viewer, an agent or another program).
/// </summary>
public sealed class GitRpc(GitService git)
{
    public static bool Handles(string method) => method is "git.check" or "git.install";

    public const string InstallRefusal = "git is installed by the person, in the app's window on this computer";

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        switch (method)
        {
            case "git.check":
                return Status(await git.CheckAsync(ct).ConfigureAwait(false));
            case "git.install":
            {
                if (Actor.Current is not { Via: Actor.App }) throw new RpcException("forbidden", InstallRefusal);
                var r = await git.InstallAsync(ct).ConfigureAwait(false);
                return new JsonObject { ["done"] = r.Done, ["message"] = r.Message, ["status"] = Status(r.Status) };
            }
            default:
                throw new RpcException("unknown_method", $"no method named '{method}'");
        }
    }

    public static JsonObject Status(GitStatus s) => new()
    {
        ["state"] = s.State,
        ["version"] = s.Version,
        ["path"] = s.Path,
        ["minimum"] = s.Minimum,
        ["canInstall"] = s.CanInstall,
        ["detail"] = s.Detail,
        ["downloadUrl"] = GitProgram.DownloadUrl,
    };
}
