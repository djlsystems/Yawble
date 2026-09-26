using System.Net;

namespace Harness.Host.Auth;

/// <summary>
/// Counts FAILED sign-ins, per account and per client address, and refuses the next attempt once
/// either has too many inside the window.
///
/// FAILURES ONLY. A successful sign-in is never counted, because one person signs in from a laptop,
/// a phone and a second browser and none of that is an attack. A success also clears that
/// account's count: whoever just proved the password is the owner.
///
/// PER ACCOUNT IS THE BOUND THAT MATTERS. Behind the ngrok tunnel every visitor reaches Kestrel
/// from the same local address (<see cref="LocalProxies"/> - X-Forwarded-For is deliberately not
/// consumed), so a per-address count is a count for everybody at once. It stays as a second,
/// much looser bound against somebody spraying many accounts, set high enough that ordinary typos
/// across a team never reach it.
///
/// A REFUSAL IS DECIDED BEFORE THE PASSWORD IS CHECKED, so during a cool-down the right password
/// is refused too. Otherwise the 429 would only apply to wrong guesses and the cool-down would be
/// an oracle: keep guessing, and the one that does not answer 429 is the password.
///
/// In memory, because the system is one process (ADR 0004). A restart forgets the counts, which
/// costs an attacker a restart they cannot cause.
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Short on purpose: long enough to make guessing slow, short enough that the owner
    /// locked out by someone else's guesses is back in within a coffee.</summary>
    public static readonly TimeSpan CoolDown = TimeSpan.FromMinutes(5);

    public const int PerAccountLimit = 10;

    /// <summary>Ten accounts' worth of lockouts from one address. Every tunnel visitor shares that
    /// address, so this is sized to stop a spray across accounts, not a person mistyping.</summary>
    public const int PerAddressLimit = 100;

    private const int PruneAbove = 10_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _addresses = new(StringComparer.Ordinal);

    /// <summary>The sentence the sign-in page shows, or null when the attempt may proceed.</summary>
    public Refusal? Check(string email, IPAddress? address)
    {
        var now = clock.GetUtcNow();

        lock (_gate)
        {
            if (Blocked(_accounts, AccountKey(email), PerAccountLimit, now) is { } accountWait)
            {
                return new Refusal(
                    $"Too many wrong passwords for this account. Wait {Minutes(accountWait)} and try again.",
                    accountWait);
            }

            if (Blocked(_addresses, AddressKey(address), PerAddressLimit, now) is { } addressWait)
            {
                return new Refusal(
                    $"Too many failed sign-ins from this network. Wait {Minutes(addressWait)} and try again.",
                    addressWait);
            }

            return null;
        }
    }

    public void Failed(string email, IPAddress? address)
    {
        var now = clock.GetUtcNow();

        lock (_gate)
        {
            Record(_accounts, AccountKey(email), now);
            Record(_addresses, AddressKey(address), now);
        }
    }

    /// <summary>Clears the account and leaves the address alone: the address is shared, and one
    /// person getting in says nothing about everyone else behind it.</summary>
    public void Succeeded(string email)
    {
        lock (_gate)
        {
            _accounts.Remove(AccountKey(email));
        }
    }

    public sealed record Refusal(string Message, TimeSpan RetryAfter);

    /// <summary>Keyed on what was TYPED, so an unknown address is counted exactly like a real one -
    /// a limit that only applied to real accounts would say which ones exist.</summary>
    private static string AccountKey(string email) => email.Trim().ToLowerInvariant();

    private static string AddressKey(IPAddress? address) =>
        address is null ? "unknown" : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    private static TimeSpan? Blocked(
        Dictionary<string, Queue<DateTimeOffset>> counts, string key, int limit, DateTimeOffset now)
    {
        if (!counts.TryGetValue(key, out var failures)) return null;

        Expire(failures, now);

        if (failures.Count < limit) return null;

        // The cool-down runs from the most recent failure. Once it is over the next attempt is let
        // through, and one more failure while the window is still full blocks again at once.
        var until = failures.Last() + CoolDown;
        return until > now ? until - now : null;
    }

    private void Record(Dictionary<string, Queue<DateTimeOffset>> counts, string key, DateTimeOffset now)
    {
        if (!counts.TryGetValue(key, out var failures))
        {
            if (counts.Count >= PruneAbove) Prune(counts, now);
            counts[key] = failures = new Queue<DateTimeOffset>();
        }

        failures.Enqueue(now);
        Expire(failures, now);
    }

    /// <summary>Anyone can type any email, so the table would otherwise grow by one entry per
    /// invented address for as long as the process lives.</summary>
    private static void Prune(Dictionary<string, Queue<DateTimeOffset>> counts, DateTimeOffset now)
    {
        foreach (var (key, failures) in counts)
        {
            Expire(failures, now);
            if (failures.Count == 0) counts.Remove(key);
        }
    }

    private static void Expire(Queue<DateTimeOffset> failures, DateTimeOffset now)
    {
        while (failures.Count > 0 && now - failures.Peek() >= Window) failures.Dequeue();
    }

    private static string Minutes(TimeSpan wait)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        return minutes == 1 ? "a minute" : $"{minutes} minutes";
    }
}
