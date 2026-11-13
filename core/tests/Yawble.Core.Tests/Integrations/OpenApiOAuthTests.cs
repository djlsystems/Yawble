using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>OAuth 2 security schemes and per-operation scopes in OpenAPI / Swagger specs (pure reads, then the stored rows).</summary>
public class OpenApiOAuthTests
{
    private static readonly Uri SpecUrl = new("https://api.example.com/docs/openapi.json");

    private static string V3(string scheme, string? topSecurity = "[{\"oa\":[\"read\"]}]", string? opSecurity = null) =>
        "{\"openapi\":\"3.0.0\",\"info\":{\"title\":\"T\"},\"servers\":[{\"url\":\"https://api.example.com\"}]," +
        "\"components\":{\"securitySchemes\":{\"oa\":" + scheme + "}}," +
        (topSecurity is null ? "" : "\"security\":" + topSecurity + ",") +
        "\"paths\":{\"/a\":{\"get\":{\"operationId\":\"a\"" + (opSecurity is null ? "" : ",\"security\":" + opSecurity) + "},\"post\":{\"operationId\":\"b\",\"security\":[{\"oa\":[\"write\",\"admin\"]}]}}}}";

    private const string CodeFlow3 =
        "{\"type\":\"oauth2\",\"flows\":{\"authorizationCode\":{\"authorizationUrl\":\"https://auth.example.com/authorize\",\"tokenUrl\":\"https://auth.example.com/token\"," +
        "\"scopes\":{\"read\":\"Read things\",\"write\":\"Change things\",\"admin\":\"Administer\"}}}}";

    [Fact]
    public void AuthorizationCodeFlowIsRead()
    {
        var spec = OpenApiReader.Read(V3(CodeFlow3), SpecUrl);
        Assert.Equal("oauth2", spec.Auth!.Type);
        Assert.Equal("https://auth.example.com/authorize", spec.Auth.AuthorizeUrl);
        Assert.Equal("https://auth.example.com/token", spec.Auth.TokenUrl);
        Assert.Equal("Read things", spec.Auth.Scopes!["read"]);
        Assert.Equal(3, spec.Auth.Scopes.Count);
        Assert.Null(spec.Auth.OpenIdConnectUrl);
    }

    [Fact]
    public void RelativeUrlsResolveAgainstTheSpecUrl()
    {
        var scheme = "{\"type\":\"oauth2\",\"flows\":{\"authorizationCode\":{\"authorizationUrl\":\"/oauth/authorize\",\"tokenUrl\":\"token\",\"scopes\":{}}}}";
        var spec = OpenApiReader.Read(V3(scheme), SpecUrl);
        Assert.Equal("https://api.example.com/oauth/authorize", spec.Auth!.AuthorizeUrl);
        Assert.Equal("https://api.example.com/docs/token", spec.Auth.TokenUrl);
    }

    [Fact]
    public void AccessCodeFlowIsRead()
    {
        const string v2 =
            "{\"swagger\":\"2.0\",\"info\":{\"title\":\"T\"},\"host\":\"api.example.com\",\"schemes\":[\"https\"]," +
            "\"securityDefinitions\":{\"oa\":{\"type\":\"oauth2\",\"flow\":\"accessCode\",\"authorizationUrl\":\"https://auth.example.com/a\",\"tokenUrl\":\"https://auth.example.com/t\",\"scopes\":{\"read\":\"Read\"}}}," +
            "\"security\":[{\"oa\":[\"read\"]}],\"paths\":{\"/a\":{\"get\":{\"operationId\":\"a\"}}}}";
        var spec = OpenApiReader.Read(v2, null);
        Assert.Equal("oauth2", spec.Auth!.Type);
        Assert.Equal("https://auth.example.com/a", spec.Auth.AuthorizeUrl);
        Assert.Equal("https://auth.example.com/t", spec.Auth.TokenUrl);
        Assert.Equal("Read", spec.Auth.Scopes!["read"]);
        Assert.Equal(["read"], spec.Actions[0].Scopes);
    }

    [Fact]
    public void OperationScopesAreRead()
    {
        var spec = OpenApiReader.Read(V3(CodeFlow3, opSecurity: "[{\"oa\":[\"read\",\"write\"]}]"), SpecUrl);
        Assert.Equal(["read", "write"], spec.Actions.Single(a => a.Id == "a").Scopes);
        Assert.Equal(["write", "admin"], spec.Actions.Single(a => a.Id == "b").Scopes);
    }

    [Fact]
    public void TopLevelSecurityGivesDefaultScopes()
    {
        var spec = OpenApiReader.Read(V3(CodeFlow3), SpecUrl);
        Assert.Equal(["read"], spec.Actions.Single(a => a.Id == "a").Scopes);
    }

    [Fact]
    public void AnEmptyOperationSecurityMeansNoScopes()
    {
        var spec = OpenApiReader.Read(V3(CodeFlow3, opSecurity: "[]"), SpecUrl);
        Assert.Empty(spec.Actions.Single(a => a.Id == "a").Scopes);
    }

    [Fact]
    public void AlternativeNamingAnotherSchemeFirstIsSkipped()
    {
        var spec = OpenApiReader.Read(V3(CodeFlow3, opSecurity: "[{\"other\":[\"x\"]},{\"oa\":[\"write\"]}]"), SpecUrl);
        Assert.Equal(["write"], spec.Actions.Single(a => a.Id == "a").Scopes);
    }

    [Fact]
    public void OperationSecurityOmittingTheOAuthSchemeGivesNoScopes()
    {
        var spec = OpenApiReader.Read(V3(CodeFlow3, opSecurity: "[{\"other\":[\"x\"]}]"), SpecUrl);
        Assert.Empty(spec.Actions.Single(a => a.Id == "a").Scopes);
    }

    [Fact]
    public void UrlsWithUserInfoOrOtherSchemesAreDropped()
    {
        var scheme = "{\"type\":\"oauth2\",\"flows\":{\"authorizationCode\":{\"authorizationUrl\":\"https://u:p@auth.example.com/a\",\"tokenUrl\":\"ftp://auth.example.com/t\",\"scopes\":{}}}}";
        var auth = OpenApiReader.Read(V3(scheme), SpecUrl).Auth!;
        Assert.Null(auth.AuthorizeUrl);
        Assert.Null(auth.TokenUrl);
    }

    [Fact]
    public void ImplicitOnlyStaysBearer()
    {
        var scheme = "{\"type\":\"oauth2\",\"flows\":{\"implicit\":{\"authorizationUrl\":\"https://auth.example.com/a\",\"scopes\":{\"read\":\"r\"}}}}";
        var spec = OpenApiReader.Read(V3(scheme), SpecUrl);
        Assert.Equal("bearer", spec.Auth!.Type);
        Assert.Null(spec.Auth.AuthorizeUrl);
        Assert.Empty(spec.Actions[0].Scopes);

        var cc = "{\"type\":\"oauth2\",\"flows\":{\"clientCredentials\":{\"tokenUrl\":\"https://auth.example.com/t\",\"scopes\":{}}}}";
        Assert.Equal("bearer", OpenApiReader.Read(V3(cc), SpecUrl).Auth!.Type);
    }

    [Fact]
    public void OpenIdConnectSchemeIsRead()
    {
        var scheme = "{\"type\":\"openIdConnect\",\"openIdConnectUrl\":\"https://auth.example.com/.well-known/openid-configuration\"}";
        var spec = OpenApiReader.Read(V3(scheme, "[{\"oa\":[\"openid\",\"email\"]}]"), SpecUrl);
        Assert.Equal("oauth2", spec.Auth!.Type);
        Assert.Equal("https://auth.example.com/.well-known/openid-configuration", spec.Auth.OpenIdConnectUrl);
        Assert.Null(spec.Auth.AuthorizeUrl);
        Assert.Equal(["openid", "email"], spec.Actions.Single(a => a.Id == "a").Scopes);
    }

    [Fact]
    public void HttpAuthorizeUrlIsDropped()
    {
        var scheme = "{\"type\":\"oauth2\",\"flows\":{\"authorizationCode\":{\"authorizationUrl\":\"http://auth.example.com/a\",\"tokenUrl\":\"https://auth.example.com/t\",\"scopes\":{}}}}";
        var spec = OpenApiReader.Read(V3(scheme), SpecUrl);
        Assert.Equal("oauth2", spec.Auth!.Type);
        Assert.Null(spec.Auth.AuthorizeUrl);
        Assert.Equal("https://auth.example.com/t", spec.Auth.TokenUrl);
    }

    [Fact]
    public void HostileScopesAreBounded()
    {
        var many = string.Join(",", Enumerable.Range(0, 500).Select(i => $"\"s{i}\":\"d\""));
        var scheme = "{\"type\":\"oauth2\",\"flows\":{\"authorizationCode\":{\"authorizationUrl\":\"https://a.example.com/a\",\"tokenUrl\":\"https://a.example.com/t\",\"scopes\":{" + many +
                     ",\"has space\":\"x\",\"" + new string('n', 201) + "\":\"x\",\"ok\":\"" + new string('d', 5000) + "\"}}}}";
        var auth = OpenApiReader.Read(V3(scheme), SpecUrl).Auth!;
        Assert.Equal(200, auth.Scopes!.Count);
        Assert.DoesNotContain("has space", auth.Scopes.Keys);
        Assert.All(auth.Scopes, kv => { Assert.True(kv.Key.Length <= 200); Assert.True(kv.Value.Length <= 300); });

        var opScopes = string.Join(",", Enumerable.Range(0, 500).Select(i => $"\"p{i}\"")) + ",\"bad scope\",\"bad\\u0001\"";
        var spec = OpenApiReader.Read(V3(CodeFlow3, opSecurity: "[{\"oa\":[" + opScopes + "]}]"), SpecUrl);
        var got = spec.Actions.Single(a => a.Id == "a").Scopes;
        Assert.Equal(200, got.Count);
        Assert.DoesNotContain("bad scope", got);
    }

    [Fact]
    public void NonOAuthSpecsHaveNoScopes()
    {
        var spec = OpenApiReader.Read(V3("{\"type\":\"http\",\"scheme\":\"bearer\"}"), SpecUrl);
        Assert.Equal("bearer", spec.Auth!.Type);
        Assert.All(spec.Actions, a => Assert.Empty(a.Scopes));
    }

    // ---- stored ----------------------------------------------------------------------------------------------------------------

    private sealed class Rig : IDisposable
    {
        public readonly TempDb Db = new();
        public readonly DiscoverStore Store;
        public readonly long ApiId;

        public Rig(string? auth = null, bool connection = false)
        {
            Store = new DiscoverStore(Db.ConnectionString, TimeProvider.System);
            if (connection)
                Exec("INSERT INTO connections (name, store_key, kind, fields, created_at, updated_at) VALUES ('c', 'k', 'secret', '[]', 'now', 'now');");
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO apis (name, base_url, auth, connection_id, created_at, updated_at) VALUES ('pets', 'https://api.example.com', $a, " +
                              (connection ? "(SELECT id FROM connections LIMIT 1)" : "NULL") + ", 'now', 'now') RETURNING id;";
            cmd.Parameters.AddWithValue("$a", (object?)auth ?? DBNull.Value);
            ApiId = Convert.ToInt64(cmd.ExecuteScalar());
        }

        public SqliteConnection Open() { var c = new SqliteConnection(Db.ConnectionString); c.Open(); return c; }

        public void Exec(string sql)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public string? Scalar(string sql)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? null : Convert.ToString(v);
        }

        public void Merge(string scheme) => Store.MergeApi(ApiId, OpenApiReader.Read(V3(scheme), SpecUrl));

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void ScopesAreStoredWithTheOperations()
    {
        using var rig = new Rig();
        rig.Merge(CodeFlow3);
        Assert.Equal("read", rig.Scalar("SELECT scopes FROM api_commands WHERE path = 'a'"));
        Assert.Equal("write admin", rig.Scalar("SELECT scopes FROM api_commands WHERE path = 'b'"));
        var auth = JsonNode.Parse(rig.Scalar("SELECT auth FROM apis WHERE name = 'pets'")!)!.AsObject();
        Assert.Equal("oauth2", (string?)auth["type"]);
        Assert.Equal("https://auth.example.com/authorize", (string?)auth["authorizeUrl"]);
        Assert.Equal("https://auth.example.com/token", (string?)auth["tokenUrl"]);
        Assert.Equal("Read things", (string?)auth["scopes"]!["read"]);
    }

    [Fact]
    public void ExistingBearerApiIsNotSwitched()
    {
        using var rig = new Rig("{\"type\":\"bearer\"}", connection: true);
        rig.Merge(CodeFlow3);
        Assert.Equal("bearer", (string?)JsonNode.Parse(rig.Scalar("SELECT auth FROM apis WHERE name = 'pets'")!)!["type"]);
        Assert.Equal("read", rig.Scalar("SELECT scopes FROM api_commands WHERE path = 'a'")); // scopes are still recorded
    }

    [Fact]
    public void RediscoveryClearsScopesWhenTheSpecDropsOAuth()
    {
        using var rig = new Rig();
        rig.Merge(CodeFlow3);
        Assert.Equal("read", rig.Scalar("SELECT scopes FROM api_commands WHERE path = 'a'"));
        rig.Merge("{\"type\":\"http\",\"scheme\":\"bearer\"}");
        Assert.Null(rig.Scalar("SELECT scopes FROM api_commands WHERE path = 'a'"));
        Assert.Null(rig.Scalar("SELECT scopes FROM api_commands WHERE path = 'b'"));
    }

    [Fact]
    public void BearerApiWithoutAConnectionBecomesOAuthOnDiscoverAgain()
    {
        using var rig = new Rig("{\"type\":\"bearer\"}");
        rig.Merge(CodeFlow3);
        Assert.Equal("oauth2", (string?)JsonNode.Parse(rig.Scalar("SELECT auth FROM apis WHERE name = 'pets'")!)!["type"]);
    }

    [Fact]
    public void AnAuthTheUserChoseIsNeverReplaced()
    {
        using var rig = new Rig("{\"type\":\"header\",\"name\":\"X-Key\",\"chosen\":true}");
        rig.Merge(CodeFlow3);
        Assert.Equal("header", (string?)JsonNode.Parse(rig.Scalar("SELECT auth FROM apis WHERE name = 'pets'")!)!["type"]);
    }
}
