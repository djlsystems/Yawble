namespace Yawble.Core;

public sealed record CoreOptions
{
    public const string Usage =
        "usage: yawble-core --ipc <socket path | pipe name> --data-dir <dir> [--install-dir <dir>] [--no-stdin-watch] [--no-control] (token in YAWBLE_IPC_TOKEN)\n" +
        "       yawble-core --version";

    public string? IpcAddress { get; init; }
    public string? DataDir { get; init; }
    /// <summary>The app's install folder (bin/, roles/); see LaunchWiring.ResolveInstallDir.</summary>
    public string? InstallDir { get; init; }
    public string? Token { get; init; }
    public bool PrintVersion { get; init; }
    public bool ExitOnStdinClose { get; init; } = true;
    /// <summary>Whether yawble-companion's control endpoint (and its control.json) is opened; --no-control turns it off.</summary>
    public bool Control { get; init; } = true;
    public string? Error { get; init; }

    /// <summary>The variable the shell hands the window's IPC token in.</summary>
    public const string TokenVariable = "YAWBLE_IPC_TOKEN";

    /// <param name="setEnv">Sets a variable of this process (null removes it). The token's variable is removed through it as
    /// soon as it is read, so no program the core starts later (a CLI integration, an MCP server, a host task, a terminal)
    /// inherits the window's token.</param>
    public static CoreOptions Parse(IReadOnlyList<string> args, Func<string, string?> env, Action<string, string?>? setEnv = null)
    {
        string? ipc = null, data = null, installDir = null;
        var stdinWatch = true;
        var control = true;
        for (var i = 0; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (args[i])
            {
                case "--version": return new CoreOptions { PrintVersion = true };
                case "--ipc": ipc = Next(); break;
                case "--data-dir": data = Next(); break;
                case "--install-dir": installDir = Next(); break;
                case "--no-stdin-watch": stdinWatch = false; break;
                case "--no-control": control = false; break;
                default: return new CoreOptions { Error = $"unknown argument: {args[i]}" };
            }
        }
        var token = env(TokenVariable);
        setEnv?.Invoke(TokenVariable, null);
        string? error = ipc is null ? "--ipc is required"
            : data is null ? "--data-dir is required"
            : string.IsNullOrEmpty(token) || token.Length < 32 ? "YAWBLE_IPC_TOKEN must be set (at least 32 characters)"
            : null;
        return new CoreOptions
        {
            IpcAddress = ipc, DataDir = data, InstallDir = installDir, Token = token,
            ExitOnStdinClose = stdinWatch, Control = control, Error = error,
        };
    }
}
