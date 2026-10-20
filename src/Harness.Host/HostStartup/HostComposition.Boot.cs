using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;
using Harness.Containers;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Capacity;
using Harness.Host.Solutions;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;
using Harness.Skills;

namespace Harness.Host;

internal sealed partial class HostComposition
{
    internal bool Boot()
    {
        builder = WebApplication.CreateBuilder(args);

        // ONE JSON OBJECT PER LINE on the console, with scopes, so a request's principal id (see
        // PrincipalLogScope) rides on every line that request logs. It is still the console, so the tee
        // above still copies it into the size-bounded host log: nothing about that file changes but its
        // line shape.
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });

        // One data root, one database, one process. Everything downstream - exactly-once delivery into a
        // container's queue, a plain polling pump instead of a broker - rests on that being true.
        //
        // TWO WAYS IN, and the explicit one wins. `DataRoot` names a folder outright, which is what the
        // operator commands, the ops scripts and every test fixture pass. `Instance` names an instance and
        // the LAYOUT decides where that lives - one Harness folder in the platform's app-data location,
        // one subfolder per instance - so a second instance is independent by construction rather than by
        // somebody remembering to set a path. Nothing set at all is `core`.
        // REMOVED FROM THE HOST'S OWN PROCESS, BEFORE ANYTHING CAN SPAWN A CHILD.
        //
        // Both spawn paths MERGE into an inherited environment, so a variable the Host holds reaches every
        // agent child whether or not this codebase names it. `AgentEnvironment.Cleared` cannot help: it is
        // a dictionary of strings and no value means absent, and for these names EMPTY IS THE DEFECT - node
        // reads a SET `FORCE_COLOR` as "force colour on" whatever its value, which puts ANSI inside the
        // payload the ledger is built from, where escape codes are tokens re-sent on every invocation.
        //
        // THIS IS THE ONLY THING THAT REACHES THE PTY PATH. Porta.Pty merges with the host environment and
        // its `Environment` option is an override dictionary - omitting a name inherits it, and no value
        // deletes it - so a Concierge child cannot be fixed at its spawn site at all. `ProcessAgentRunner`
        // removes them again for headless children, as defence in depth rather than as the mechanism.
        //
        // Safe for the Host itself: `FORCE_COLOR` is a NODE convention and nothing in this solution reads
        // it, so no .NET behaviour changes, its own console included.
        foreach (var inherited in AgentEnvironment.MustBeAbsent)
        {
            if (Environment.GetEnvironmentVariable(inherited) is null) continue;

            // SAID OUT LOUD, because a silent removal is one nobody can find when they go looking for why
            // their terminal lost its colour - and because this is the operator's own shell being
            // overridden, which they are entitled to know about.
            Environment.SetEnvironmentVariable(inherited, null);
            Console.WriteLine(
                $"Removed {inherited} from this Host's environment so agent children do not inherit it.");
        }

        // WHAT THIS PROCESS IS: all (control with its own run worker, the default), or control alone with
        // its runs on workers that connect. A worker never gets here: it runs from WorkerRole, before this
        // method is compiled. An unknown role is refused there for a real start, and here for a test Host.
        var (role, roleRefusal) = HostRoles.Resolve([], builder.Configuration["Role"] ?? builder.Configuration[HostRoles.Variable]);
        if (roleRefusal is not null || role == HostRole.Worker)
        {
            Console.Error.WriteLine(roleRefusal ?? "--Role worker starts before the Host composes anything; this Host was composed, so it stops.");
            Environment.ExitCode = 2;
            return false;
        }

        control = role == HostRole.Control;

        dataRoot = builder.Configuration["DataRoot"]
            ?? Environment.GetEnvironmentVariable("HARNESS_DATA_ROOT")!;

        if (dataRoot is null)
        {
            // Non-zero, for the reason every other refusal in this file is: a supervisor told "served and
            // shut down cleanly" restarts straight back into it.
            Console.Error.WriteLine("No data root is set: set HARNESS_DATA_ROOT or --DataRoot and start again.");
            Environment.ExitCode = 1;
            return false;
        }

        Directory.CreateDirectory(dataRoot);

        // THE EARLIEST POINT A FILE CAN BE OPENED, and deliberately the earliest one: the root now exists,
        // and everything below here that can fail - an
        // operator command, a migration, a schema from the future, Kestrel refusing a port - fails INTO the
        // file rather than into a window. What was printed before this line is replayed, so the lines that
        // answer "what was this instance even configured as" are not the ones the ordering costs.
        //
        // The budget is DERIVED OR DECLARED, never a constant: `HostLog:Budget` if an operator names one,
        // otherwise a hundredth of what is free where it lands, which is how it degrades on a small host
        // instead of refusing. It is not in appsettings.json on purpose - a size shipped in the repository
        // is a number tuned to one machine. See HostLogBudget.
        hostLog.OpenIn(dataRoot, builder.Configuration["HostLog:Budget"]);

        // What tells this instance from every other one on the machine. Derived from the data root, which
        // is the instance boundary the line above declares - see InstanceIdentity. It composes with the
        // layout rather than competing with it: the root is a function of the instance name, so the
        // readable half of the cookie name comes out as that name with nothing coordinating the two.
        instance = InstanceIdentity.For(dataRoot);

        database = Path.Combine(dataRoot, "messages.db");

        return true;
    }
}
