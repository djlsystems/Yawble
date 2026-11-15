using System.Text;

namespace Yawble.GuardCheck;

/// <summary>The smallest DNS message the raw DNS probes need: one A question, recursion desired (RFC 1035 4.1).</summary>
public static class DnsQuery
{
    public static byte[] Build(string name, ushort id)
    {
        var labels = name.TrimEnd('.').Split('.');
        if (labels.Any(l => l.Length is 0 or > 63 || !l.All(c => c < 128)))
            throw new ArgumentException($"'{name}' is not a plain DNS name");
        var message = new List<byte>
        {
            (byte)(id >> 8), (byte)id,
            0x01, 0x00, // standard query, RD
            0x00, 0x01, // QDCOUNT 1
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // AN, NS, AR
        };
        foreach (var label in labels)
        {
            message.Add((byte)label.Length);
            message.AddRange(Encoding.ASCII.GetBytes(label));
        }
        message.Add(0);
        message.AddRange([0x00, 0x01, 0x00, 0x01]); // QTYPE A, QCLASS IN
        if (message.Count > 512) throw new ArgumentException($"'{name}' is too long for a DNS question");
        return message.ToArray();
    }

    /// <summary>An answer to <paramref name="query"/>: a full header with its id and the QR (response) bit set.</summary>
    public static bool Matches(ReadOnlySpan<byte> query, ReadOnlySpan<byte> answer) =>
        answer.Length >= 12 && answer[0] == query[0] && answer[1] == query[1] && (answer[2] & 0x80) != 0;

    public static string Describe(ReadOnlySpan<byte> answer)
    {
        var rcode = answer[3] & 0x0F;
        var count = (answer[6] << 8) | answer[7];
        var name = rcode switch { 0 => "NOERROR", 2 => "SERVFAIL", 3 => "NXDOMAIN", 5 => "REFUSED", _ => "rcode " + rcode };
        return $"{name}, {count} answer record(s)";
    }
}
