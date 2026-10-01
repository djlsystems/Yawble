using System.Security.Cryptography;
using System.Text;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A SHIPPED SCHEMA STEP IS NEVER EDITED.
///
/// The migrator records only a step's id, so a database that applied `auth-001` never runs an edited
/// `auth-001` again: the edit reaches fresh volumes and silently misses every existing one. A change
/// is a NEW step. This holds the SQL of every step that has shipped to the hash it shipped with;
/// adding a step adds a line here, editing one fails.
/// </summary>
public sealed class ShippedSchemaStepsTests
{
    private static readonly Dictionary<string, string> Shipped = new(StringComparer.Ordinal)
    {
        ["messages-001"] = "405bfd0b5d7180af4cd6844c31d5d6a2e58c2cc385aaae83a5798b782e3feb9a",
        ["messages-002"] = "968d861608751e96c040aa158f04f16baf6053ce464f8e44ee2c2ecc177b2ccb",
        ["auth-001"] = "c2e86a37e99a689f95c32134db882b3add91e4775c1516c759ee1758482d374d",
        ["auth-002"] = "e1b6b1e9b3dab445585d7658667e95067126053948aaf4a0e3ebfd6f500605b4",
        ["auth-003"] = "a632d0adfee1aae846161316f552e5d72a91a586935a7c51dc45c39d624869d5",
        ["auth-004"] = "e2568b08eac35edae23585337a69afd7e81758164974393ccced3e509653d908",
        ["auth-005"] = "acdc38710039bf6fdf6f0916375316cab9d8389222213da6dd064c5e5eb4d5dc",
        ["auth-006"] = "563d0590c714b8f41c4ee044e5f10d4958c6eb25283856560968b49233c0f154",
        ["auth-007"] = "07d2f36f8efc56eef9795b16a1a0916dc10a940e332319e13172f2850e53e852",
        ["auth-008"] = "5b8fea9abcdfb0991c75cdc16e6bee6647f4afd3800901742596e341b6c37267",
        ["auth-009"] = "aa246076e2fc37a18c918f298f5a40dee6184aa6b481a99d212f5044a3ec8e42",
        ["auth-010"] = "3792da5a8054294685b96c84a58a2d7511f2d0d3ea5461af2ca4a8c34eb11a51",
        ["auth-011"] = "fcc48a9774a2038ef6afa855e57c7e1e16b251dd0b2d8adaed6018d8d2664f02",
        ["auth-012"] = "822c52ee63ad02f764b5661206bc2f53b15c0908488b92f53c2bcf79347881ae",
        ["auth-013"] = "060cfc7c0c306afe29e38c3bdfa5d6bac048e60755c717c80ccf6f5033112514",
        ["auth-014"] = "77e2022a5cb1be8b21415e25983cc3bf5d335777cce6d5896476f0375c7c2450",
        ["auth-015"] = "497f239926ec5681da82086039f4c1c2b443a9ebae395fac882f4568b99b0688",
        ["auth-016"] = "f31ed44966364dfccf9180712042720026bcc73a928360c4ac3b8e70d611ae52",
        ["auth-017"] = "c2fabc7e78fca1862491b5a802c920f1b0a9518ec7cbada8a4310edc892566fe",
        ["skill-001"] = "43d1e6d741db4f371cbc11722e5c782c62892118b1f480206a8961e28b011d0e",
        ["skill-002"] = "c489c729453566533b3d2301a0e04b90025074284dc9b78dce8847991abcc726",
        ["skill-003"] = "c5b39099b5250853f952d26dca3479df8edf54cc9a7e24a71baffb2660c390b6",
        ["skill-004"] = "3646834471a60d3a1b2a6429d6127d7690bb15c391c4380d7ca5c5d03f21085b",
        ["backlog-001"] = "5441f611093ed242108fe8766197adb7d17b988269749b63a05362ee410afdf3",
        ["backlog-002"] = "52f896b58b5822468ae8398ba8e5bb1813b1654e47d8ad5195029666ea537bb1",
        ["backlog-003"] = "41bf0b9ea83429d4b7b1335046930346a4d843ea576ac78ec8b13eb629758c22",
        ["outcome-001"] = "34255e6957695d42d110a914f70e1365e435c2f2b1193ca190c5f35fa05274a5",
        ["outcome-002"] = "69641ad2d4e8ef79e39022ee0f731b472f39a6fddac99c1edfabad53f73bce25",
        ["outcome-003"] = "a96629f93b989f033aca444053f2033e2bd69458d6e1051cd181c33d87ce9797",
    };

    [Fact]
    public void A_shipped_step_is_never_edited()
    {
        var drift = new List<string>();

        foreach (var step in SchemaModules.All)
        {
            var hash = Hash(step.Sql);

            if (!Shipped.TryGetValue(step.Id, out var pinned))
            {
                drift.Add($"[\"{step.Id}\"] = \"{hash}\",  // new step: add this line");
            }
            else if (pinned != hash)
            {
                drift.Add($"{step.Id} was edited after it shipped (was {pinned}, now {hash}). "
                    + "Revert it and add a new step instead.");
            }
        }

        Assert.True(drift.Count == 0, string.Join(Environment.NewLine, drift));
    }

    [Fact]
    public void Every_pinned_step_is_still_handed_to_the_migrator()
    {
        var ids = SchemaModules.All.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        Assert.All(Shipped.Keys, id => Assert.Contains(id, ids));
    }

    // Line endings normalised so a checkout's autocrlf cannot read as an edit.
    private static string Hash(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n"))));
}
