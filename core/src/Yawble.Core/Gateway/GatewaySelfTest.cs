using System.Net.Sockets;
using System.Text;

namespace Yawble.Core.Gateway;

/// <summary>
/// What the doctor's "gateway" line really checks. Gateways are per run, so none listens while the app is idle; the check starts
/// a throwaway one from the same <see cref="GatewayHost"/> the runs use, sends it one request over its listener (a real loopback socket in production) and
/// requires the gateway's own refusal back (a host it does not serve gets 403). It reaches no upstream, reads no credential,
/// writes nothing to the gateway log, and is stopped and its token revoked before it returns.
/// </summary>
public static class GatewaySelfTest
{
    /// <summary>A run number no real run has (run numbers count up from 1).</summary>
    public const long SelfTestRunId = long.MinValue + 1;

    public const string UnservedHost = "doctor-selftest.yawble.internal";

    /// <summary>Null when the gateway answered as it should; otherwise the reason it did not.</summary>
    public static async Task<string?> CheckAsync(GatewayHost host, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        RunGateway? gateway = null;
        try
        {
            gateway = host.Start(new RunGatewaySpec(SelfTestRunId, "doctor/self-test", new HashSet<string>(), [], TimeSpan.FromMinutes(1))
                { HostSignIn = true, Quiet = true }, new NoBusEndpoint());
            await using var stream = await gateway.OpenClientAsync(cts.Token);
            var request = $"GET http://{UnservedHost}/ HTTP/1.1\r\nHost: {UnservedHost}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);
            var buffer = new byte[256];
            var read = 0;
            while (read < 12)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), cts.Token);
                if (n == 0) break;
                read += n;
            }
            var line = Encoding.ASCII.GetString(buffer, 0, read);
            return line.StartsWith("HTTP/1.1 403", StringComparison.Ordinal) ? null : $"it answered '{line.Split('\r')[0]}' instead of refusing an unserved host";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"no answer within {timeout.TotalSeconds:0} s";
        }
        catch (Exception e) when (e is SocketException or IOException or InvalidOperationException)
        {
            return $"could not reach it on loopback ({e.GetType().Name})";
        }
        finally
        {
            if (gateway is not null) await gateway.DisposeAsync();
            host.Tokens.Forget(SelfTestRunId);
        }
    }

    private sealed class NoBusEndpoint : IBusEndpoint
    {
        public Task<BusReply> HandleAsync(byte[] body, CancellationToken ct) => Task.FromResult(new BusReply(503, null));
    }
}
