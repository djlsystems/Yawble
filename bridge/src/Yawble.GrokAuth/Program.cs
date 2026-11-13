// yawble-grok-auth: the placeholder auth provider for Grok in login mode. Grok runs it through
// GROK_AUTH_PROVIDER_COMMAND, takes stdout as its token, and refreshes by running it again (GROK_AUTH_EXPIRED=1).
// It prints the run token, which is only the gateway's placeholder: the gateway swaps in the person's Grok session,
// which never enters the sandbox. It reads nothing else, opens no network connection and has no other mode.
// Output: the bare token on one line, the form Grok accepts.
var token = Environment.GetEnvironmentVariable("YAWBLE_RUN_TOKEN");
if (string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine("yawble-grok-auth: no YAWBLE_RUN_TOKEN in the environment (it runs only inside a Yawble run)");
    return 1;
}
Console.Out.Write(token + "\n");
Console.Out.Flush();
return 0;
