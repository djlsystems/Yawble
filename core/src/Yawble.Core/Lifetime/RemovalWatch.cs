using System.Text.Json.Nodes;

namespace Yawble.Core.Lifetime;

/// <summary>
/// About once a minute: was this core's own executable deleted (an uninstall), or is the data folder at its path no longer
/// the one it locked (removed, or replaced)? While either holds, each check publishes <see cref="EventName"/> so the shell
/// shows "Yawble was uninstalled or its data was removed" with a Quit button (a shell that connects later still hears it
/// within a minute). The OS facts come through <see cref="IInstallProbe"/>.
/// </summary>
public sealed class RemovalWatch(IInstallProbe probe, Action<string, JsonNode?> publish, Action<string>? log = null, TimeSpan? interval = null)
{
    public const string EventName = "app.removed";
    public const string Message = "Yawble was uninstalled or its data was removed";
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    private bool _logged;

    /// <summary>One check: the notice it published, or null when everything is still in place.</summary>
    public RemovalNotice? CheckOnce()
    {
        bool exeDeleted, dataChanged;
        try
        {
            exeDeleted = probe.ExecutableDeleted();
            dataChanged = probe.DataFolderChanged();
        }
        catch (Exception e)
        {
            log?.Invoke($"removal check: {e.GetType().Name}: {e.Message}");
            return null; // a check that cannot run says nothing; the next one tries again
        }
        if (!exeDeleted && !dataChanged) return null;
        var notice = new RemovalNotice(exeDeleted, dataChanged);
        if (!_logged)
        {
            _logged = true;
            log?.Invoke($"{Message} (executable deleted: {exeDeleted}, data folder changed: {dataChanged})");
        }
        publish(EventName, new JsonObject
        {
            ["message"] = Message,
            ["executableDeleted"] = exeDeleted,
            ["dataFolderChanged"] = dataChanged,
        });
        return notice;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval ?? DefaultInterval, ct).ConfigureAwait(false);
                CheckOnce();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

public sealed record RemovalNotice(bool ExecutableDeleted, bool DataFolderChanged);

/// <summary>The two OS facts the removal watch reads.</summary>
public interface IInstallProbe
{
    bool ExecutableDeleted();
    bool DataFolderChanged();
}

/// <summary>The raw OS reads behind <see cref="SystemInstallProbe"/>: faked in tests.</summary>
public interface IInstallFacts
{
    bool IsLinux { get; }
    /// <summary>Where a symbolic link points (/proc/self/exe), or null when it cannot be read.</summary>
    string? LinkTarget(string path);
    string? ProcessPath { get; }
    bool FileExists(string path);
}

public sealed class SystemInstallFacts : IInstallFacts
{
    public static readonly SystemInstallFacts Instance = new();
    public bool IsLinux => OperatingSystem.IsLinux();
    public string? LinkTarget(string path)
    {
        try { return new FileInfo(path).LinkTarget; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    public string? ProcessPath => Environment.ProcessPath;
    public bool FileExists(string path) => File.Exists(path);
}

/// <summary>
/// Linux: /proc/self/exe names the running executable and the kernel appends " (deleted)" once its file is gone (a
/// replaced file shows the same). Elsewhere: the executable's path no longer names a file. The data folder: the lock's own
/// check (<see cref="DataFolderLock.StillOurs"/>).
/// </summary>
public sealed class SystemInstallProbe(Func<bool> dataFolderStillOurs, IInstallFacts? facts = null) : IInstallProbe
{
    private const string ProcSelfExe = "/proc/self/exe";
    private const string DeletedSuffix = " (deleted)";
    private readonly IInstallFacts _facts = facts ?? SystemInstallFacts.Instance;

    public bool ExecutableDeleted()
    {
        if (_facts.IsLinux && _facts.LinkTarget(ProcSelfExe) is { } target)
            return target.EndsWith(DeletedSuffix, StringComparison.Ordinal);
        return _facts.ProcessPath is { Length: > 0 } exe && !_facts.FileExists(exe);
    }

    public bool DataFolderChanged() => !dataFolderStillOurs();
}
