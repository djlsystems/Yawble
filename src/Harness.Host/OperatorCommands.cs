using Harness.Identity;
using Harness.Messaging;

namespace Harness.Host;

/// <summary>
/// What an operator at the machine can do, and a running system cannot.
///
/// Every command here opens the data root, does one thing, prints what it did and returns true,
/// which stops Program.cs before Kestrel starts. NONE of them is a route and none is a CLI verb -
/// that is the point, not an accident of packaging. The CLI is on the PATH of every container and
/// console child, all running with permissions bypassed, so a recovery verb there would be one an
/// agent could run. `--doctor` is the one command a program reads rather than a person; its report
/// is the last line of stdout.
///
/// Takes its output as a TextWriter so a test can read what an operator would see, rather than
/// asserting against a process's stdout.
///
/// A misspelt switch ("--bakcup") is NOT refused - it falls through and the host serves. "Starts
/// with --" cannot tell a typo of an operator switch apart from ASP.NET Core's own "--key value"
/// configuration idiom (`--urls http://localhost:8090` is exactly that shape), and refusing one
/// refuses the other. These commands must never serve; catching typos is not their job, so
/// letting arbitrary configuration
/// through is the trade this takes: a misspelling starts a server, which is VISIBLE (a banner, a
/// listening URL, a process that keeps running instead of exiting), rather than a silent refusal
/// that only a person reading stdout would ever see.
/// </summary>
/// <summary>
/// What an operator switch did, and therefore what the process should exit with.
///
/// Three states rather than two, because "a command ran" and "a command succeeded" are different
/// questions and only the caller can answer the second one with an exit code. A REFUSAL that exits
/// 0 tells a supervisor - and <c>scripts/backup.ps1</c> and <c>scripts/restore.ps1</c>, which both
/// read <c>$LASTEXITCODE</c> - that the host served and shut down cleanly, so it restarts straight
/// back into the same refusal.
///
/// It covers every refusal, not only the exception paths (a schema from the future, a failed
/// backup, a failed step): a restore refused because a host is running, a reset for an account
/// that is not there, a switch missing its value.
/// </summary>
public enum OperatorOutcome
{
    /// <summary>Not an operator switch at all - the host should serve.</summary>
    NotAnOperatorCommand,

    /// <summary>The command did what it was asked.</summary>
    Completed,

    /// <summary>The command refused, having changed nothing. The process must exit non-zero.</summary>
    Refused,
}

public static class OperatorCommands
{
    /// <summary>
    /// The operator switches this binary recognises, by their key alone (before any "="). Everything
    /// below reads this set rather than naming a switch a second time, which is what keeps
    /// recognition and dispatch from drifting apart as more are added.
    /// </summary>
    private static readonly HashSet<string> KnownSwitches = new(StringComparer.Ordinal)
    {
        "--backup",
        "--restore",
        "--reset-password",
        "--list-users",
        "--doctor",
    };

    private const string Usage =
        """
        Operator commands (the host does not serve when one is given):

          --backup [path]            Write a consistent copy of the database.
          --restore <path>           Replace the database with a backup. Refuses while a host runs.
          --reset-password <email>   Set a new random password and print it once.
          --list-users               List every account, so a lost one can be named.
          --doctor                   Print one JSON line describing this data root. Safe while a host runs.
        """;

    /// <summary>True when the arguments were an operator command and the host must not serve.</summary>
    public static async Task<OperatorOutcome> TryRunAsync(
        string[] args, string dataRoot, TextWriter output, CancellationToken ct = default)
    {
        // ANYWHERE in the arguments, not only first. "--DataRoot=X --backup" is exactly the shape an
        // operator reaches for - and it is the shape scripts/backup.ps1 and scripts/restore.ps1
        // use, because the data root has to precede a valueless switch or ASP.NET's command-line
        // provider swallows it as that switch's value. Reading args[0] alone would make that spelling
        // SERVE instead of backing up, which is the worst possible answer to "back this up": a running
        // host, no backup, and a zero exit code.
        var (key, value) = FindSwitch(args);

        // Unknown or absent: not ours, whatever shape the arguments have - including a legitimate
        // ASP.NET config flag like "--urls http://localhost:8090", which starts with "--", carries no
        // "=", and must still reach Kestrel rather than being refused as a typo. See the class doc
        // comment.
        if (key is null) return OperatorOutcome.NotAnOperatorCommand;

        switch (key)
        {
            case "--backup":
                return await BackUpAsync(Path.Combine(dataRoot, "messages.db"), dataRoot, value, output, ct)
                    ? OperatorOutcome.Completed
                    : OperatorOutcome.Refused;

            case "--restore":
                return await RestoreAsync(Path.Combine(dataRoot, "messages.db"), dataRoot, value, output, ct)
                    ? OperatorOutcome.Completed
                    : OperatorOutcome.Refused;

            case "--reset-password":
                return await ResetPasswordAsync(Path.Combine(dataRoot, "messages.db"), value, output, ct)
                    ? OperatorOutcome.Completed
                    : OperatorOutcome.Refused;

            case "--list-users":
                return await ListUsersAsync(Path.Combine(dataRoot, "messages.db"), output, ct)
                    ? OperatorOutcome.Completed
                    : OperatorOutcome.Refused;

            case "--doctor":
                return await DoctorAsync(dataRoot, output, ct)
                    ? OperatorOutcome.Completed
                    : OperatorOutcome.Refused;

            default:
                // Unreachable: KnownSwitches and this switch name the same set, by construction -
                // getting here would mean the two were allowed to drift apart.
                throw new InvalidOperationException($"No handler wired for known switch '{key}'.");
        }
    }

    /// <summary>
    /// The first operator switch in the arguments, with its value, or (null, null) when there is
    /// none.
    ///
    /// Split on the FIRST "=" so "--backup=path" and "--backup path" are the same switch with its
    /// value carried two different ways, and recognition happens on the KEY alone - never on whether
    /// a whole argument matches something we know. Matching the whole argument would
    /// misread "--environment=Development" (the form WebApplicationFactory&lt;Program&gt; uses to hand
    /// every host test fixture its settings) as a typo of an operator switch.
    /// </summary>
    private static (string? Key, string? Value) FindSwitch(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            // Only arguments STARTING with "--" are candidates at all. Everything else is
            // configuration for the host proper, which must go on serving.
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;

            var separator = args[i].IndexOf('=');
            var key = separator < 0 ? args[i] : args[i][..separator];

            if (!KnownSwitches.Contains(key)) continue;

            // The "=" spelling wins if both are somehow present ("--backup=here there"): that reads
            // as one destination, not two, and treating the trailing word as a second positional
            // argument would be guessing at intent instead of doing what was typed.
            if (separator >= 0) return (key, args[i][(separator + 1)..]);

            // A following argument is this switch's value only if it is not itself a switch. Without
            // that, "--backup --DataRoot=X" would back up to a file literally named "--DataRoot=X".
            var next = i + 1 < args.Length ? args[i + 1] : null;

            return (key, next?.StartsWith("--", StringComparison.Ordinal) == true ? null : next);
        }

        return (null, null);
    }

    private static async Task<bool> BackUpAsync(
        string database, string dataRoot, string? destination, TextWriter output,
        CancellationToken ct)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");

        var path = destination
            ?? Path.Combine(dataRoot, "backups", $"messages-{stamp}-manual.db");

        // Safe while a host is running: WAL permits a concurrent reader and VACUUM INTO takes a
        // consistent snapshot. A Copy-Item here would produce a file missing everything still in
        // the -wal.
        await SchemaMigrator.BackUpAsync(database, path, ct);

        output.WriteLine($"Wrote {path}");

        return true;
    }

    /// <summary>
    /// Replaces the database with a backup. The three refusals below are all cases where doing it
    /// anyway looks like success.
    /// </summary>
    private static async Task<bool> RestoreAsync(
        string database, string dataRoot, string? source, TextWriter output, CancellationToken ct)
    {
        if (source is null)
        {
            output.WriteLine("--restore needs the path of a backup to restore.");
            output.WriteLine();
            output.WriteLine(Usage);
            return false;
        }

        if (!File.Exists(source))
        {
            output.WriteLine($"There is no backup at '{source}'. Nothing was changed.");
            return false;
        }

        // A running host holds the file. Overwriting it underneath a live process corrupts a
        // database that was fine, so this asks first rather than discovering it halfway through.
        //
        // THREE checks, deliberately, because none alone fires reliably against a real serving host:
        //   - IsHeld opens the database file exclusively. On Windows this catches almost nothing
        //     here - SQLite opens its own files with a share mode permissive enough that this still
        //     succeeds while a real host is serving requests through the very same file. It stays
        //     because it catches a NON-SQLite holder (an editor, a sync tool) nothing else sees.
        //   - DataRootLock.IsHeld asks whether a host holds THIS DATA ROOT, which is the actual
        //     question, and is what fires in practice.
        //   - SchemaMigrator.IsInUseAsync asks SQLite for its own write lock, which catches a write
        //     in flight (a migration, a live transaction) that the others could paper over.
        //
        // None of them scans for ANY Harness.Host process on the machine: that would be
        // data-root-BLIND, and a host serving an entirely different database would refuse this
        // restore. DataRootLock says which root a host holds, so one instance never interferes with
        // another.
        //
        // Verified against a REAL running host, not a simulation - see OperatorCommandTests's comment
        // on why the in-process unit test cannot be the thing that proves this.
        if (File.Exists(database)
            && (IsHeld(database)
                || DataRootLock.IsHeld(dataRoot)
                || await SchemaMigrator.IsInUseAsync(database, ct)))
        {
            var holder = DataRootLock.HeldBy(dataRoot);

            output.WriteLine(
                $"'{database}' is in use - a host is running"
                + (holder is null ? "" : $" (process {holder})")
                + ". Stop it and try again. Nothing was changed.");
            return false;
        }

        if (File.Exists(database))
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");

            // The pooled connection this opens against `database` is released by BackUpAsync itself
            // before returning - see the comment there - so the File.Copy and sidecar deletes below
            // never race a handle this same process is still holding.
            var taken = await SchemaMigrator.BackUpAsync(
                database,
                Path.Combine(dataRoot, "backups", $"messages-{stamp}-before-restore.db"),
                ct);

            // SAID, not merely done. The whole justification for this copy is that the database
            // about to be overwritten may be the only copy of something - which is worth nothing to
            // an operator who cannot find it afterwards. Printed before the restore line so the
            // order on screen matches the order of events.
            output.WriteLine($"Backed up the current database to {taken}");
        }

        File.Copy(source, database, overwrite: true);

        // A stale WAL is replayed over whatever the file now holds, which produces a database that
        // is neither the backup nor what was there before.
        foreach (var sidecar in new[] { database + "-wal", database + "-shm" })
        {
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }

        output.WriteLine($"Restored {database} from {source}.");

        return true;
    }

    /// <summary>
    /// Sets a new random password and prints it ONCE.
    ///
    /// No forced change at next login: the profile screen already changes a password, so a
    /// must_change_password column would buy a migration and an SPA state for something the
    /// existing screen covers.
    /// </summary>
    private static async Task<bool> ResetPasswordAsync(
        string database, string? email, TextWriter output, CancellationToken ct)
    {
        if (email is null)
        {
            output.WriteLine("--reset-password needs the email address of an account.");
            output.WriteLine();
            output.WriteLine(Usage);
            return false;
        }

        // The schema first: this command's whole purpose is reaching a database whose owner cannot
        // start the host, and that database may predate the current shape.
        await MigrateAsync(database, output, ct);

        var store = new SqliteUserStore(database);
        var user = await store.FindAsync(email, ct);

        if (user is null)
        {
            await ListAccountsAsync(store, email, output, ct);
            return false;
        }

        var password = NewPassword();

        await store.UpdatePasswordAsync(user.Id, password, ct);

        output.WriteLine($"The password for {user.Email} is now:");
        output.WriteLine(password);

        return true;
    }

    /// <summary>
    /// Every account, so an operator can NAME one.
    ///
    /// This exists to reopen a recovery path that a single mistake shuts. `--reset-password` takes
    /// an email address; when a member bootstraps an instance with a credential it invented, that
    /// address lives only in a context that has since exited, and registration is closed for good.
    /// Nothing could then get back in. Listing the accounts costs nothing and makes that recoverable.
    ///
    /// A HOST SWITCH AND NEVER A CLI VERB. The CLI is on the PATH of every container and console
    /// child, all running with permissions bypassed, so `harness list-users` would hand every
    /// agent in the tenant the list of accounts to attack. Identical reasoning to `--reset-password`
    /// being a switch, and the reason `OperatorCommands` is not a route either.
    /// </summary>
    private static async Task<bool> ListUsersAsync(
        string database, TextWriter output, CancellationToken ct)
    {
        await MigrateAsync(database, output, ct);

        var users = await new SqliteUserStore(database).ListAsync(ct);

        if (users.Count == 0)
        {
            // Not a refusal. An instance nobody has bootstrapped is a legitimate state - it is what
            // `needsAdmin` reports - and exiting non-zero here would tell a supervisor the command
            // failed when it answered correctly.
            output.WriteLine("No accounts. This instance has not been bootstrapped.");
            return true;
        }

        foreach (var user in users.OrderBy(u => u.Email, StringComparer.Ordinal))
        {
            output.WriteLine(user.Email);
        }

        return true;
    }
    /// <summary>
    /// One JSON object, on ONE line, as the LAST thing written. The operator CLI runs this with an
    /// exec in the container and reads the last line of stdout, because the Host may print before
    /// its operator commands run (the FORCE_COLOR removal in Program.cs does). Every other command
    /// here prints prose; this one is read by a program.
    ///
    /// Completed whenever a report was written. A report that says "no database" or "schema not
    /// accepted" is a correct answer, not a refusal - the CLI decides what is a failing check.
    /// Safe against a live host for the same reason --backup is: it reads and never writes, and
    /// Program.cs runs operator commands before it takes the data-root lock.
    /// </summary>
    private static async Task<bool> DoctorAsync(string dataRoot, TextWriter output, CancellationToken ct)
    {
        var report = await HostDoctor.ReportAsync(dataRoot, ct);

        output.WriteLine(HostDoctor.ToJson(report));

        return true;
    }

    /// <summary>
    /// The schema, and what moving it cost. EVERY module's steps, always, from
    /// <see cref="SchemaModules.All"/> - ApplyAsync computes "unknown" as applied-minus-what-it-was-
    /// handed, so a partial list against a database that has more than that is refused as a downgrade
    /// (see its own remarks). A hand-built list here would fall behind the first time a module was
    /// added, which is why there is one shared list rather than a copy.
    ///
    /// Says where the backup went for the same reason Program.cs does: this is a recovery command,
    /// run by someone whose instance is already not working, and a copy of their database appearing
    /// in a folder they never asked about is worth saying out loud.
    /// </summary>
    private static async Task MigrateAsync(string database, TextWriter output, CancellationToken ct)
    {
        var backup = await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct);

        if (backup is not null) output.WriteLine($"Schema updated. Backup written to {backup}");
    }

    private static async Task ListAccountsAsync(
        SqliteUserStore store, string missing, TextWriter output, CancellationToken ct)
    {
        var accounts = await store.ListAsync(ct);

        // An EMPTY data root says so, rather than promising a list and printing nothing. That
        // silence would hide a mistyped data root: "The accounts here are:" followed by nothing
        // reads as "my account is missing" when the truth is "this is a database that has never
        // held an account".
        if (accounts.Count == 0)
        {
            output.WriteLine(
                $"There is no account for '{missing}' - this data root has no accounts at all.");
            output.WriteLine(
                "That usually means the wrong data root, not a missing account. Check the path.");

            return;
        }

        output.WriteLine($"There is no account for '{missing}'. The accounts here are:");

        foreach (var account in accounts) output.WriteLine($"  {account.Email}");
    }

    /// <summary>
    /// Long and random rather than memorable. It is typed once, into a login form, by someone who
    /// has it on the screen in front of them - and it is about to be changed.
    /// </summary>
    private static string NewPassword() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

    /// <summary>Whether another process holds the file. An exclusive open is the question actually
    /// being asked, and asking it is cheaper than any lock-file convention nobody else follows.
    /// Kept alongside the process and SQLite-lock checks above: it catches a non-SQLite, non-
    /// Harness-Host holder (an editor, a sync tool) that neither of those would ever see.
    /// </summary>
    private static bool IsHeld(string path)
    {
        try
        {
            using var probe = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
