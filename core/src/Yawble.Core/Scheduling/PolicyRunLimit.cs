using Yawble.Core.Policy;

namespace Yawble.Core.Scheduling;

/// <summary>
/// The bus's run limit read from the global policy's app.maxConcurrentAgents: one number for every team,
/// default 4. It is read at every admission, so a saved policy takes effect at the next admission (call
/// <see cref="Bus.BusEngine.Reschedule"/> after saving to admit at once). A raised limit admits more queued entries;
/// a lowered one never stops a running sandbox. A value the policy check would refuse, or an unreadable policy,
/// falls back to the default rather than stopping all admissions.
/// </summary>
public static class PolicyRunLimit
{
    public static Func<int> From(string connectionString, Action<string>? log = null)
    {
        var store = new PolicyStore(connectionString);
        var warned = false;
        return () =>
        {
            try
            {
                var limit = store.GetGlobal().App.MaxConcurrentAgents;
                if (limit is >= 1 and <= FloorRules.MaxConcurrentAgentsCeiling) return limit;
                if (!warned) log?.Invoke($"app.maxConcurrentAgents {limit} is outside 1..{FloorRules.MaxConcurrentAgentsCeiling}; using {RunScheduler.DefaultMaxConcurrentAgents}");
                warned = true;
            }
            catch (Exception ex)
            {
                if (!warned) log?.Invoke($"the global policy could not be read ({ex.Message}); run limit {RunScheduler.DefaultMaxConcurrentAgents}");
                warned = true;
            }
            return RunScheduler.DefaultMaxConcurrentAgents;
        };
    }

    /// <summary>app.heavyWorkAtOnce of the saved global policy (default 1), read at every lease call; 1 when unreadable.</summary>
    public static Func<int> HeavyFrom(string connectionString)
    {
        var store = new PolicyStore(connectionString);
        return () =>
        {
            try
            {
                var n = store.GetGlobal().App.HeavyWorkAtOnce;
                return n is >= 1 and <= Bus.BusEngine.MaxHeavyAtOnce ? n : 1;
            }
            catch (Exception)
            {
                return 1;
            }
        };
    }
}
