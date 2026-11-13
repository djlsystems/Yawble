using System.Reflection;
using System.Text;
using Yawble.Bridge;

// yawble-bridge: the stdio MCP server each agent CLI launches inside its sandbox. It relays MCP to
// http://bus.yawble.internal/mcp through the run's gateway, found from HTTP_PROXY (the sandbox injects it; on Linux it reads
// http://10.0.2.2:<port>), with the run token from YAWBLE_RUN_TOKEN. Identity is the gateway's, not anything in a call.
var version = typeof(GatewayAddress).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion.Split('+')[0] ?? "0.0.0";
if (args is ["--version"])
{
    Console.WriteLine(version);
    return 0;
}

if (args is ["--shell"])
{
    // A local MCP server with a run_shell tool (ShellServer); no gateway needed.
    var shellIn = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    var shellOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
    try
    {
        await new ShellServer(Environment.CurrentDirectory).RunAsync(shellIn, shellOut, Console.Error, CancellationToken.None);
    }
    catch (OperationCanceledException)
    {
    }
    return 0;
}

var gateway = GatewayAddress.FromEnvironment(Environment.GetEnvironmentVariable, out var error);
if (gateway is null)
{
    Console.Error.WriteLine($"yawble-bridge {version}: {error}");
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var relay = new BusRelay(gateway);
var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
try
{
    await relay.RunAsync(stdin, stdout, Console.Error, stop.Token);
}
catch (OperationCanceledException)
{
}
return 0;
