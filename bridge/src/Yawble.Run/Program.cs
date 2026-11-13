using System.Diagnostics;

// yawble-run <program> [arguments...]
//
// Runs inside a member's sandbox in front of an agent CLI. On Windows, a ProcessContainer's TEMP/TMP are set by
// Windows to <LOCALAPPDATA>\Packages\<container>\AC\Temp, a folder that does not exist until something creates it, and
// whose container name changes every run (so the host cannot make it beforehand). A CLI that writes there without
// creating it first fails: Grok 1.0.77's interactive session cannot make its session folder, and every tool call then
// fails "workspace session not found ... call bind_local_session() first". So: make the temp folder, then run the CLI
// with the same arguments in the same console (the run's PTY), and exit with its exit code.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: yawble-run <program> [arguments...]");
    return 2;
}
try
{
    Directory.CreateDirectory(Path.GetTempPath());
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"yawble-run: could not create the temp folder {Path.GetTempPath()}: {e.Message}");
}

var psi = new ProcessStartInfo(args[0]) { UseShellExecute = false };
foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
// Ctrl+C belongs to the CLI (it shares this console); this process only waits for it.
Console.CancelKeyPress += (_, e) => e.Cancel = true;
try
{
    using var child = Process.Start(psi)!;
    child.WaitForExit();
    return child.ExitCode;
}
catch (System.ComponentModel.Win32Exception e)
{
    Console.Error.WriteLine($"yawble-run: could not start {args[0]}: {e.Message}");
    return 127;
}
