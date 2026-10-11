namespace Harness.Tests;

/// <summary>A fixture <c>/proc</c> tree: one entry per process, its <c>stat</c> naming its group and its <c>statm</c> its resident pages.</summary>
internal static class FixtureProc
{
    public static void Write(string proc, int pid, int group, long residentPages, long utime = 10, long stime = 5, string comm = "agent-cli")
    {
        var entry = Directory.CreateDirectory(Path.Combine(proc, pid.ToString(System.Globalization.CultureInfo.InvariantCulture))).FullName;
        File.WriteAllText(Path.Combine(entry, "stat"),
            $"{pid} ({comm}) S 1 {group} {group} 0 -1 4194560 100 0 0 0 {utime} {stime} 0 0 20 0 1 0 12345 1000000 {residentPages}\n");
        File.WriteAllText(Path.Combine(entry, "statm"), $"9000 {residentPages} 100 10 0 500 0\n");
    }

    public static void Remove(string proc, int pid) =>
        Directory.Delete(Path.Combine(proc, pid.ToString(System.Globalization.CultureInfo.InvariantCulture)), recursive: true);
}
