using System.Net;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.HttpOverrides;

namespace Harness.Host;

/// <summary>
/// Which peers may tell this host the request arrived over https: THIS MACHINE'S OWN ADDRESSES.
///
/// Under rootless Podman with pasta, ngrok on Windows dials 127.0.0.1:8080, WSL relays that into
/// the machine's loopback, and pasta hands it to the container FROM THE CONTAINER'S OWN ADDRESS
/// (its eth0 address). Loopback alone would therefore see every tunnel request as http://. On a host with no container the tunnel dials loopback, which is also
/// one of this machine's addresses, so one rule covers both.
///
/// Proto only. A process inside the container could also claim https; what that buys
/// is a Secure cookie on a plain-http connection, which the browser will not send back.
/// </summary>
public static class LocalProxies
{
    public static ForwardedHeadersOptions Options(IEnumerable<IPAddress> ownAddresses)
    {
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto };

        // The default list holds ::1 only. IPv4 loopback is added explicitly, and each address is
        // added in both forms because a dual-stack socket reports an IPv4 peer as ::ffff:a.b.c.d.
        foreach (var address in ownAddresses.Append(IPAddress.Loopback))
        {
            options.KnownProxies.Add(address);
            if (address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork)
                options.KnownProxies.Add(address.MapToIPv6());
        }

        return options;
    }

    /// <summary>Every unicast address on this machine's interfaces that are up. Empty if the
    /// platform will not say, which leaves loopback as the only trusted peer.</summary>
    public static IReadOnlyList<IPAddress> OwnAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus is OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
