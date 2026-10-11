using System.ComponentModel;

// The catalog's own type, kept in its namespace: the run protocol carries it, so both sides see it.
namespace Harness.Host;

/// <summary>
/// Where an agent that picks its own session id leaves its transcript: the newest file under
/// <see cref="Folder"/> matching <see cref="Pattern"/>, written at or after the launch, whose
/// session belongs to the run's workspace by <see cref="CwdFrom"/>'s rule.
/// </summary>
public sealed record AgentLiveViewFind(
    [property: Description("The folder searched. It must start with `~/` and may not contain `..`.")]
    string Folder,
    [property: Description(
        "The file, relative to the folder, one `/`-separated segment per level, each a name that "
        + "may use `*` and `?` (`*/*/*/rollout-*.jsonl`). May not contain `..`.")]
    string Pattern,
    [property: Description(
        "How a candidate's workspace is read: `folder-name` (the first folder under `folder`, "
        + "percent-decoded), `workspace-yaml` (the `cwd:` line of `workspace.yaml` beside the file) "
        + "or `first-line-cwd` (the `cwd` of the file's first JSON line, or of its `payload`).")]
    string CwdFrom);
