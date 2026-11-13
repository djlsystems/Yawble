using System.Text;
using Yawble.Companion;
using Yawble.Core.Companion;

// yawble: the companion client of the running Yawble app (CLI, or `yawble mcp` for Claude Code). UTF-8 without a BOM on
// every stream, "\n" line ends: with `mcp`, stdout is the protocol and nothing else.
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
var stdin = new StreamReader(Console.OpenStandardInput(), utf8);

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

Func<string, string?> env = Environment.GetEnvironmentVariable;
string home;
try
{
    home = ControlDiscovery.Home(env);
}
catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
{
    home = ""; // Cli reports the unusable YAWBLE_HOME itself
}

return await Cli.RunAsync(args, env, stdin, stdout, stderr, new PipeConnector(home), stop.Token);
