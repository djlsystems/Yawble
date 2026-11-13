using System.Text;
using System.Text.Json;
using Yawble.GuardCheck;

// yawble-guardcheck: the guardrail check's probe (a battery of escape attempts, run as an in-app check).
// The core runs it INSIDE a member's sandbox, under that member's exact compiled sandbox request with only the command
// line swapped:
//   yawble-guardcheck --plan <base64url JSON>
// It tries each escape the plan names and prints one JSON line per attempt to stdout:
//   {"id":"...","category":"...","outcome":"blocked|allowed|skip|info","evidence":"...", ...}
// "allowed" here means only "this worked from inside the sandbox". The core decides the final verdict: a write that
// worked inside the sandbox but never reached the host is BLOCKED (sandbox release 1.0.1 masks denied folders with a writable
// tmpfs). The probe never prints, copies or reads the content of a secret: a sensitive file counts as reachable if it
// can be OPENED (open + close, no bytes read); only the core's own planted canaries are read.
if (args.Length != 2 || args[0] != "--plan")
{
    Console.Error.WriteLine("usage: yawble-guardcheck --plan <base64url JSON>");
    return 2;
}

ProbePlan plan;
try
{
    plan = ProbePlan.Parse(Encoding.UTF8.GetString(Base64Url.Decode(args[1])));
}
catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
{
    Console.Error.WriteLine("yawble-guardcheck: the plan is not valid: " + e.Message);
    return 2;
}

using var stdout = Console.OpenStandardOutput();
var output = new ProbeOutput(stdout);
await new Probes(plan, output, Environment.GetEnvironmentVariable).RunAllAsync();
return 0;
