using System.Text;
using System.Text.Json;
using Yawble.GuardCheck;

// yawble-guardcheck: the guardrail check's probe. The core runs it inside a member's sandbox, under that member's
// compiled sandbox request with only the command line swapped (yawble-guardcheck --plan <base64url JSON>). It tries
// each escape the plan names and prints one JSON line per attempt:
//   {"id":"...","category":"...","outcome":"blocked|allowed|skip|info","evidence":"...", ...}
// "allowed" means only "this worked from inside the sandbox"; the core decides the verdict. The probe never prints,
// copies or reads a secret: a sensitive file counts as reachable if it can be opened (no bytes read).
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
