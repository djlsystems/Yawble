using System.Text.Json.Nodes;
using Yawble.Core.Integrations;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>The OpenAPI/Swagger reader. Pure text in, actions out; nothing is fetched.</summary>
public class OpenApiReaderTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Integrations", "Fixtures", "openapi", name));

    private static ApiAction Act(ApiSpec spec, string id) => spec.Actions.Single(a => a.Id == id);

    [Fact]
    public void Reads_V3Json_ActionsTagsAndClasses()
    {
        var spec = OpenApiReader.Read(Fixture("petstore-v3.json"), null);
        Assert.Equal("Petstore", spec.Title);
        Assert.Equal("https://eu.petstore.example/v1", spec.ServerUrl);
        Assert.Equal(["listPets", "createPets", "deletePet"], spec.Actions.Select(a => a.Id));

        var list = Act(spec, "listPets");
        Assert.Equal("GET", list.Method);
        Assert.Equal("/pets", list.Route);
        Assert.Equal("pets", list.Tag);
        Assert.Equal("List all pets", list.Summary);
        Assert.Equal(CommandClass.ReadOnly, list.Class.Class);
        Assert.Equal("method", list.Class.Source);
        var props = (JsonObject)list.Params["properties"]!;
        Assert.NotNull(props["limit"]);
        Assert.Contains("(in query)", (string)props["limit"]!["description"]!);
        Assert.NotNull(props["X-Trace"]);
        Assert.Null(props["Authorization"]);

        Assert.Equal(CommandClass.Change, Act(spec, "createPets").Class.Class);
        Assert.NotNull(((JsonObject)Act(spec, "createPets").Params["properties"]!)["body"]);
        Assert.Contains("body", Act(spec, "createPets").Params["required"]!.AsArray().Select(n => (string)n!));
        var del = Act(spec, "deletePet");
        Assert.Equal(CommandClass.Destructive, del.Class.Class);
        Assert.Contains("petId", del.Params["required"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("header", spec.Auth!.Type);
        Assert.Equal("X-Api-Key", spec.Auth.Name);
        Assert.Equal(0, spec.Dropped);
    }

    [Fact]
    public void Reads_V2Yaml_ServerUrlFromHostAndBasePath()
    {
        var text = Fixture("petstore-v2.yaml");
        Assert.True(OpenApiReader.LooksLikeSpec(text));
        var spec = OpenApiReader.Read(text, null);
        Assert.Equal("https://api.example.com/v2", spec.ServerUrl);
        Assert.Equal("Petstore Two", spec.Title);
        Assert.Equal(2, spec.Actions.Count);
        Assert.Equal("integer", (string)((JsonObject)Act(spec, "listPets").Params["properties"]!)["limit"]!["type"]!);
        Assert.NotNull(((JsonObject)Act(spec, "addPet").Params["properties"]!)["body"]);
        Assert.Equal("basic", spec.Auth!.Type);
    }

    [Fact]
    public void NoOperationIds_UsesMethodAndRoute()
    {
        var a = OpenApiReader.Read(Fixture("no-operation-ids.yaml"), null);
        var b = OpenApiReader.Read(Fixture("no-operation-ids.yaml"), null);
        Assert.Equal(["GET /pets", "GET /pets/{petId}", "PUT /pets/{petId}"], a.Actions.Select(x => x.Id));
        Assert.Equal(a.Actions.Select(x => x.Id), b.Actions.Select(x => x.Id));
    }

    [Fact]
    public void Bearer_And_ApiKeyHeader_Auth()
    {
        var bearer = OpenApiReader.Read(Fixture("bearer.json"), new Uri("https://docs.example.com/spec/openapi.json"));
        Assert.Equal("bearer", bearer.Auth!.Type);
        Assert.Null(bearer.Auth.Name);
        Assert.Equal("Paste a token", bearer.Auth.Description);
        Assert.Equal("https://docs.example.com/api", bearer.ServerUrl);

        var header = OpenApiReader.Read(Fixture("petstore-v3.json"), null);
        Assert.Equal(("header", "X-Api-Key"), (header.Auth!.Type, header.Auth.Name));

        var query = OpenApiReader.Read("""{"openapi":"3.0.0","paths":{},"components":{"securitySchemes":{"k":{"type":"apiKey","in":"query","name":"api_key"}}}}""", null);
        Assert.Equal(("query", "api_key"), (query.Auth!.Type, query.Auth.Name));
        var cookie = OpenApiReader.Read("""{"openapi":"3.0.0","paths":{},"components":{"securitySchemes":{"k":{"type":"apiKey","in":"cookie","name":"sid"}}}}""", null);
        Assert.Null(cookie.Auth);
        var oauth = OpenApiReader.Read("""{"openapi":"3.1.0","paths":{},"components":{"securitySchemes":{"o":{"type":"oauth2","flows":{}}}}}""", null);
        Assert.Equal("bearer", oauth.Auth!.Type);
    }

    [Fact]
    public void CredentialRoutes_AreNever()
    {
        var spec = OpenApiReader.Read(Fixture("bearer.json"), null);
        var c = Act(spec, "makeToken").Class;
        Assert.Equal(CommandClass.Credential, c.Class);
        Assert.Equal("floor", c.Source);
        Assert.Equal(CommandClass.ReadOnly, Act(spec, "me").Class.Class);
        Assert.Equal("POST /user/tokens", $"{Act(spec, "makeToken").Method} {Act(spec, "makeToken").Route}");

        var viaParam = OpenApiReader.Read("""{"openapi":"3.0.0","paths":{"/x/{Password}":{"get":{}}}}""", null);
        Assert.Equal(CommandClass.Credential, viaParam.Actions[0].Class.Class);
    }

    [Fact]
    public void Refs_Resolved_AndCyclesStop()
    {
        var spec = OpenApiReader.Read(Fixture("refs.json"), null);
        var add = Act(spec, "addNode");
        var props = (JsonObject)add.Params["properties"]!;
        Assert.NotNull(props["page"]);
        var body = (JsonObject)props["body"]!;
        Assert.Equal("object", (string)body["type"]!);
        // The cycle Node -> next -> Node -> ... unrolls a bounded number of times, then becomes {}.
        var depth = 0;
        JsonNode? cur = body;
        while (cur is JsonObject o && o["properties"]?["next"] is JsonObject next) { cur = next; depth++; }
        Assert.InRange(depth, 1, 9);
        Assert.Empty((JsonObject)cur!);
        // A remote ref becomes {}.
        Assert.Empty((JsonObject)((JsonObject)Act(spec, "remoteRef").Params["properties"]!)["body"]!);
    }

    [Fact]
    public void NotASpec_Throws()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.Read("<!doctype html><html><body>Docs</body></html>", null));
        Assert.Contains("not an OpenAPI", ex.Message);
        Assert.False(OpenApiReader.LooksLikeSpec("<html></html>"));
        Assert.False(OpenApiReader.LooksLikeSpec("{\"hello\": 1}"));
        Assert.False(OpenApiReader.LooksLikeSpec(""));
        Assert.True(OpenApiReader.LooksLikeSpec("swagger: '2.0'\npaths: {}"));
    }

    [Fact]
    public void Caps_At2000()
    {
        var paths = new JsonObject();
        for (var i = 0; i < 2100; i++) paths[$"/r{i}"] = new JsonObject { ["get"] = new JsonObject { ["operationId"] = $"op{i}" } };
        var doc = new JsonObject { ["openapi"] = "3.0.0", ["paths"] = paths };
        var spec = OpenApiReader.Read(doc.ToJsonString(), null);
        Assert.Equal(2000, spec.Actions.Count);
        Assert.Equal(100, spec.Dropped);
        Assert.Equal("op0", spec.Actions[0].Id);
        Assert.Equal("op1999", spec.Actions[^1].Id);
    }

    [Fact]
    public void DuplicateOperationIds_FallBackToMethodAndRoute()
    {
        var spec = OpenApiReader.Read("""{"openapi":"3.0.0","paths":{"/a":{"get":{"operationId":"x"}},"/b":{"get":{"operationId":"x"}}}}""", null);
        Assert.Equal(["GET /a", "GET /b"], spec.Actions.Select(a => a.Id));
    }

    private static JsonObject PropsOf(ApiAction a) => (JsonObject)a.Params["properties"]!;

    private static string RefDoc(JsonObject schemas, string start) => new JsonObject
    {
        ["openapi"] = "3.0.0",
        ["paths"] = new JsonObject { ["/n"] = new JsonObject { ["post"] = new JsonObject { ["requestBody"] = new JsonObject { ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/" + start } } } } } } },
        ["components"] = new JsonObject { ["schemas"] = schemas },
    }.ToJsonString();

    // ---- hostile input: each of these overflows the stack inside YamlDotNet without the pre-scan -------------------------------

    [Fact]
    public void DeepFlowYaml_IsRefusedPlainly()
    {
        var text = "openapi: 3.0.0\nx: " + new string('[', 10_000);
        var ex = Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.Read(text, null));
        Assert.Equal("the spec is too deeply nested to read", ex.Message);
        Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.LooksLikeSpec(text));
    }

    [Fact]
    public void DeepBlockYaml_IsRefusedPlainly()
    {
        var sb = new System.Text.StringBuilder("openapi: 3.0.0\n");
        for (var i = 0; i < 300; i++) sb.Append(' ', i * 2).Append('k').Append(i).Append(":\n");
        var ex = Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.Read(sb.ToString(), null));
        Assert.Equal("the spec is too deeply nested to read", ex.Message);
        Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.LooksLikeSpec(sb.ToString()));
    }

    [Fact]
    public void DeepJson_IsRefusedPlainly()
    {
        var text = "{\"openapi\":\"3.0.0\",\"x\":" + new string('[', 300) + new string(']', 300) + "}";
        var ex = Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.Read(text, null));
        Assert.Equal("the spec is too deeply nested to read", ex.Message);
    }

    [Fact]
    public void YamlAliasBomb_SaysTooLarge()
    {
        var sb = new System.Text.StringBuilder("openapi: 3.0.0\nl0: &l0 [a, a, a, a, a, a, a, a, a, a]\n");
        for (var i = 1; i < 9; i++) sb.Append($"l{i}: &l{i} [").Append(string.Join(", ", Enumerable.Repeat($"*l{i - 1}", 10))).Append("]\n");
        var ex = Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.Read(sb.ToString(), null));
        Assert.Equal("the spec is too large or uses too many YAML aliases to read", ex.Message);
    }

    [Fact]
    public void SyntaxError_SaysCouldNotBeRead()
    {
        var bad = "openapi: 3.0.0\npaths: [unclosed\n  - : :\n";
        var ex = Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.Read(bad, null));
        Assert.StartsWith("the spec could not be read as YAML or JSON: ", ex.Message);
        var js = Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.Read("{\"openapi\": ", null));
        Assert.StartsWith("the spec could not be read as YAML or JSON: ", js.Message);
        Assert.False(OpenApiReader.LooksLikeSpec(bad));
    }

    // ---- $ref expansion ------------------------------------------------------------------------------------------------------

    [Fact]
    public void SelfReferencingProperties_StaySmallAndFast()
    {
        var node = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        for (var i = 0; i < 7; i++) node["properties"]![$"p{i}"] = new JsonObject { ["$ref"] = "#/components/schemas/Node" };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var spec = OpenApiReader.Read(RefDoc(new JsonObject { ["Node"] = node }, "Node"), null);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(spec.Actions[0].Params.ToJsonString().Length < 200_000);
    }

    [Fact]
    public void MutuallyReferencingSchemas_AreCutByTheNodeBudget()
    {
        var schemas = new JsonObject();
        for (var s = 0; s < 8; s++)
        {
            var props = new JsonObject();
            for (var t = 0; t < 8; t++) props[$"p{t}"] = new JsonObject { ["$ref"] = $"#/components/schemas/S{t}" };
            schemas[$"S{s}"] = new JsonObject { ["type"] = "object", ["properties"] = props };
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var spec = OpenApiReader.Read(RefDoc(schemas, "S0"), null);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(spec.Actions[0].Params.ToJsonString().Length < 200_000);
    }

    // ---- form bodies ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void FormBodies_V2FormData_AndV3Urlencoded()
    {
        var v2 = OpenApiReader.Read("""
            swagger: "2.0"
            paths:
              /upload:
                post:
                  operationId: up
                  parameters:
                    - {name: file, in: formData, type: string, required: true}
                    - {name: note, in: formData, type: string, description: A note}
            """, null);
        var a = v2.Actions[0];
        Assert.Equal("form", (string)a.Params["x-body"]!);
        var body = (JsonObject)PropsOf(a)["body"]!;
        Assert.Equal("object", (string)body["type"]!);
        Assert.NotNull(body["properties"]!["file"]);
        Assert.Equal(["file"], body["required"]!.AsArray().Select(n => (string)n!));
        Assert.Contains("body", a.Params["required"]!.AsArray().Select(n => (string)n!));

        var v3 = OpenApiReader.Read("""{"openapi":"3.0.0","paths":{"/f":{"post":{"operationId":"f","requestBody":{"content":{"application/x-www-form-urlencoded":{"schema":{"type":"object","properties":{"name":{"type":"string"}}}}}}}}}}""", null);
        Assert.Equal("form", (string)v3.Actions[0].Params["x-body"]!);
        Assert.NotNull(PropsOf(v3.Actions[0])["body"]!["properties"]!["name"]);

        // A JSON body wins over a form body; no body gives no x-body.
        var both = OpenApiReader.Read("""{"openapi":"3.0.0","paths":{"/f":{"post":{"operationId":"f","requestBody":{"content":{"multipart/form-data":{"schema":{"type":"object"}},"application/json":{"schema":{"type":"object","properties":{"j":{"type":"string"}}}}}}}}}}""", null);
        Assert.Equal("json", (string)both.Actions[0].Params["x-body"]!);
        Assert.NotNull(PropsOf(both.Actions[0])["body"]!["properties"]!["j"]);
        Assert.False(OpenApiReader.Read(Fixture("petstore-v3.json"), null).Actions[0].Params.ContainsKey("x-body"));
    }

    // ---- name collisions -----------------------------------------------------------------------------------------------------

    [Fact]
    public void NameCollisions_GetSuffixAndKeepTheRealNameAndLocation()
    {
        var spec = OpenApiReader.Read("""
            {"openapi":"3.0.0","paths":{"/things/{id}":{"post":{"operationId":"t",
              "parameters":[
                {"name":"id","in":"path","required":true,"schema":{"type":"string"}},
                {"name":"id","in":"query","schema":{"type":"string"}},
                {"name":"body","in":"query","schema":{"type":"string"}}],
              "requestBody":{"content":{"application/json":{"schema":{"type":"object"}}}}}}}}
            """, null);
        var a = spec.Actions[0];
        var props = PropsOf(a);
        Assert.Equal("path", (string)props["id"]!["x-in"]!);
        Assert.Equal("id", (string)props["id"]!["x-name"]!);
        Assert.Equal("query", (string)props["id_query"]!["x-in"]!);
        Assert.Equal("id", (string)props["id_query"]!["x-name"]!);
        Assert.Contains("(query parameter 'id')", (string)props["id_query"]!["description"]!);
        Assert.Equal("body", (string)props["body_query"]!["x-name"]!);
        Assert.Equal("object", (string)props["body"]!["type"]!);
        var req = a.Params["required"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.Equal(req.Distinct().Count(), req.Count);
    }

    // ---- ServerUrl -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"openapi":"3.0.0","paths":{},"servers":[{"url":"file:///etc"}]}""")]
    [InlineData("""{"openapi":"3.0.0","paths":{},"servers":[{"url":"ftp://x.example/"}]}""")]
    [InlineData("""{"swagger":"2.0","paths":{},"host":"a.example","schemes":["ws"]}""")]
    [InlineData("""{"swagger":"2.0","paths":{},"host":"a.example/x?y","schemes":["https"]}""")]
    [InlineData("""{"openapi":"3.0.0","paths":{},"servers":[{"url":"/relative"}]}""")]
    public void ServerUrl_IsOnlyAbsoluteHttp(string json) => Assert.Null(OpenApiReader.Read(json, null).ServerUrl);

    [Fact]
    public void NoServers_WithSpecUrl_IsTheOriginAndSlash()
    {
        var spec = OpenApiReader.Read("""{"openapi":"3.0.0","paths":{}}""", new Uri("https://docs.example.com:8443/a/b/openapi.json"));
        Assert.Equal("https://docs.example.com:8443/", spec.ServerUrl);
    }

    // ---- parameters ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void PathLevelParameter_IsOverriddenByOperationParameterWithSameNameAndIn()
    {
        var spec = OpenApiReader.Read("""
            {"openapi":"3.0.0","paths":{"/a":{
              "parameters":[{"name":"limit","in":"query","description":"path level","schema":{"type":"string"}},{"name":"q","in":"query","schema":{"type":"string"}}],
              "get":{"operationId":"a","parameters":[{"name":"limit","in":"query","description":"op level","schema":{"type":"integer"}}]}}}}
            """, null);
        var props = PropsOf(spec.Actions[0]);
        Assert.Equal(2, props.Count);
        Assert.Equal("integer", (string)props["limit"]!["type"]!);
        Assert.Contains("op level", (string)props["limit"]!["description"]!);
    }

    [Fact]
    public void V2_RefParameter_IsResolved()
    {
        var spec = OpenApiReader.Read("""
            swagger: "2.0"
            parameters:
              Lim:
                name: limit
                in: query
                type: integer
                required: true
            paths:
              /a:
                get:
                  operationId: a
                  parameters:
                    - $ref: "#/parameters/Lim"
            """, null);
        var a = spec.Actions[0];
        Assert.Equal("integer", (string)PropsOf(a)["limit"]!["type"]!);
        Assert.Equal(["limit"], a.Params["required"]!.AsArray().Select(n => (string)n!));
    }

    // ---- fix round 2: bypasses of the first guard (each overflowed the stack inside YamlDotNet) -------------------------------

    [Fact]
    public void ApostropheInFlowContext_DoesNotHideNesting()
    {
        var text = "openapi: 3.0.0\nx: [a'b, " + new string('[', 20_000);
        var ex = Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.Read(text, null));
        Assert.Equal("the spec is too deeply nested to read", ex.Message);
        Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.LooksLikeSpec(text));
    }

    [Fact]
    public void OneLineBlockSequences_AreCounted()
    {
        var text = "openapi: 3.0.0\nx:\n  " + string.Concat(Enumerable.Repeat("- ", 20_000)) + "a\n";
        var ex = Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.Read(text, null));
        Assert.Equal("the spec is too deeply nested to read", ex.Message);
        Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.LooksLikeSpec(text));
    }

    [Fact]
    public void OneLineComplexKeys_AreCounted()
    {
        var text = "openapi: 3.0.0\n" + string.Concat(Enumerable.Repeat("? ", 20_000)) + "a\n";
        var ex = Assert.ThrowsAny<ArgumentException>(() => OpenApiReader.Read(text, null));
        Assert.Equal("the spec is too deeply nested to read", ex.Message);
    }

    [Fact]
    public void MarkdownLinksAndApostrophes_StillRead()
    {
        var spec = OpenApiReader.Read("""
            openapi: 3.0.0
            info:
              title: Docs
              description: See [the guide](https://example.com/guide) - it's [great](https://example.com/x), isn't it?
            paths:
              /a:
                get:
                  operationId: a
                  summary: Gets the user's [thing](https://example.com/t)
                  description: "Don't [panic]: it's fine"
            """, null);
        Assert.Equal("Docs", spec.Title);
        Assert.Equal("Gets the user's [thing](https://example.com/t)", spec.Actions[0].Summary);
    }
}
