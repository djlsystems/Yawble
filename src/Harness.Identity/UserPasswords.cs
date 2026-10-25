using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Harness.Identity;

/// <summary>
/// THE PASSWORD HALF OF A USER STORE, whichever database holds the rows: the hasher, the hash of a
/// password nobody holds, and the one verification. Both providers' user stores hold one, so a hash
/// either writes is the same string and verifies on the other; only the SQL around it differs.
///
/// PasswordHasher does the security-relevant part - the algorithm, the salt, the iteration count -
/// and it arrives without EF Core, which is precisely why this codebase can decline to own password
/// hashing without also acquiring a second storage mechanism.
/// </summary>
public sealed class UserPasswords
{
    private readonly PasswordHasher<string> _hasher;

    /// <summary>
    /// A hash of a password nobody holds, verified against when there is no such user.
    ///
    /// Without it an unknown email returns in microseconds while a known one pays a full PBKDF2
    /// verification - tens of milliseconds, trivially measurable across a network, on an anonymous
    /// endpoint that an instance may expose through a tunnel. That
    /// timing difference answers "does this account exist?", which is exactly the question
    /// VerifyAsync's return value is careful never to answer.
    ///
    /// Hashed once per store rather than per call: the point is to spend the same work as a real
    /// verification, not to spend it twice. There is one store per process, so "once per store" costs
    /// the same as a `static readonly` would.
    ///
    /// It MUST come from <see cref="_hasher"/> rather than from a hasher of its own. The iteration
    /// count is embedded in the hash and verification reads it from there, so a hash made by a
    /// DIFFERENT hasher costs whatever that one was configured for - which is the timing difference
    /// this field exists to remove, reintroduced through the field itself.
    /// </summary>
    private readonly string _unknownUserHash;

    /// <summary>
    /// What a store built without <c>hashIterations</c> uses - i.e. what production uses.
    ///
    /// READ from the framework rather than restated as a literal, and that is the point of it
    /// existing at all: a copy of "100,000" here would be a second store of a fact that Microsoft
    /// owns and raises between releases (it was 10,000 before .NET 8), and the copy would go stale
    /// silently while reading as deliberate. Anything comparing against the default - the start-up
    /// warning, the spec that pins it - asks this and gets whatever the framework actually does.
    /// </summary>
    public static int DefaultHashIterations { get; } = new PasswordHasherOptions().IterationCount;

    /// <param name="hashIterations">
    /// PBKDF2 iterations, or null for the framework default - which is what production passes and
    /// what every caller that forgets this parameter gets. It exists for the test host: the default
    /// is ~100,000 iterations of HMAC-SHA512, deliberately, and the Host suite pays two of them per
    /// fixture across ~490 fixtures, which measured at over half that suite's total runtime.
    ///
    /// Optional-with-a-null-default is safe HERE in a way this codebase is usually right to
    /// distrust, and the reason is the direction it fails in: omitting it gives you the STRONG
    /// hasher. A parameter whose absence weakens a credential would be the shape to refuse.
    /// </param>
    public UserPasswords(int? hashIterations = null)
    {
        _hasher = hashIterations is null
            ? new PasswordHasher<string>()
            : new PasswordHasher<string>(Options.Create(
                new PasswordHasherOptions { IterationCount = hashIterations.Value }));

        _unknownUserHash = _hasher.HashPassword("", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Trimmed and lowercased. An identifier a person types is the same identifier
    /// whatever case they typed it in, and the UNIQUE index has to see that too or two accounts
    /// differing only by case both exist.</summary>
    public static string Normalise(string email) => email.Trim().ToLowerInvariant();

    /// <summary>
    /// The text stored in <c>users.password_hash</c>.
    ///
    /// The hash is NOT bound to <paramref name="user"/> despite HashPassword taking one:
    /// PasswordHasher&lt;T&gt; ignores its user argument entirely - it salts per call and stores the
    /// salt in the hash. That is why changing an address does not invalidate a password.
    /// </summary>
    public string Hash(string user, string password) => _hasher.HashPassword(user, password);

    /// <summary>
    /// Whether <paramref name="password"/> is the one <paramref name="stored"/> was made from; null
    /// <paramref name="stored"/> is a user that does not exist, and is verified against the unknown
    /// user's hash so this branch costs what the other does.
    /// </summary>
    public bool Verify(string normalisedEmail, string? stored, string password)
    {
        if (stored is null)
        {
            // Deliberate work on a path that could return immediately - see _unknownUserHash. The
            // result is discarded because it cannot be anything but Failed; what matters is that
            // this branch costs what the branch below costs.
            _ = _hasher.VerifyHashedPassword(normalisedEmail, _unknownUserHash, password);
            return false;
        }

        return _hasher.VerifyHashedPassword(normalisedEmail, stored, password) != PasswordVerificationResult.Failed;
    }
}
