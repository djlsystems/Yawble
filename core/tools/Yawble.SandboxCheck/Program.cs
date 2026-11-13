using Yawble.SandboxCheck;

// The sandbox release gate. Fails (exit 1) when the chosen sandbox release is a
// pre-release, or no longer accepts a 1.0.0 request with telemetry off, so the person is asked first.
//   dotnet run --project core/tools/Yawble.SandboxCheck [-- --offline] [--repo <root>]
var offline = args.Contains("--offline");
var repoIndex = Array.IndexOf(args, "--repo");
var root = repoIndex >= 0 && repoIndex + 1 < args.Length ? args[repoIndex + 1] : ReleaseGate.FindRepoRoot(Environment.CurrentDirectory);
if (root is null)
{
    Console.Error.WriteLine("the repository root (Yawble.slnx + sandbox.lock.json) was not found above the current folder; pass --repo <root>");
    return 2;
}

var report = await ReleaseGate.RunAsync(root, offline, new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
foreach (var line in report.Lines)
{
    var mark = line.Level switch { Level.Pass => "PASS", Level.Warn => "WARN", Level.Fail => "FAIL", _ => "INFO" };
    Console.WriteLine($"{mark}  {line.Text}");
}
Console.WriteLine(report.Failed ? "Sandbox release gate: FAILED - ask the person before adopting this release." : "Sandbox release gate: passed.");
return report.Failed ? 1 : 0;
