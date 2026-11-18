namespace Yawble.Core.Bus;

/// <summary>A member's first name as stored (0046 members.given_name), for the used-name check: null when its name had none.</summary>
public sealed record GivenNameRow(long MemberId, string Name, string? GivenName);

/// <summary>The person's saved defaults (0046 app_defaults) and the members' first names (0046 members.given_name).</summary>
public sealed partial class BusStore
{
    /// <summary>A saved default's JSON value, or null when it was never saved.</summary>
    public string? AppDefault(string key) => Scalar<string?>("SELECT value FROM app_defaults WHERE key = $k;", ("$k", key));

    /// <summary>Saves a default's JSON value; null removes it (back to the built-in default).</summary>
    public void SetAppDefault(string key, string? json)
    {
        if (json is null)
        {
            Exec("DELETE FROM app_defaults WHERE key = $k;", ("$k", key));
            return;
        }
        Exec("""
            INSERT INTO app_defaults (key, value, updated_at) VALUES ($k, $v, $at)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """, ("$k", key), ("$v", json), ("$at", Now()));
    }

    /// <summary>Every member that is not a Manager, of every team (archived ones too), with its first name.</summary>
    public IReadOnlyList<GivenNameRow> GivenNames()
    {
        using var cmd = Command("SELECT id, name, given_name FROM members WHERE role = 'member' ORDER BY id;");
        using var r = cmd.ExecuteReader();
        var list = new List<GivenNameRow>();
        while (r.Read()) list.Add(new GivenNameRow(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return list;
    }
}
