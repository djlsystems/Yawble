using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Integrations;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Card 613 (B143 part 4): what integration_calls.args keeps of an API's or Email's arguments (<see cref="CallArgsRedaction"/>).</summary>
public class CallArgsRedactionTests
{
    private static string R(string json) => CallArgsRedaction.Redact(JsonNode.Parse(json)!.AsObject()).ToJsonString();

    [Fact]
    public void ContentFields_KeepOnlyTheirLength_WhateverTheirCase()
    {
        Assert.Equal("""{"Body":"(5 chars)","subject":"(3 chars)","html_body":"(2 chars)","Message":"(1 chars)"}""",
            R("""{"Body":"Hello","subject":"Hi!","html_body":"<p","Message":"x"}"""));
    }

    [Fact]
    public void ContentUnderAName_IsContentAllTheWayDown_ButTheRequestBodyObjectIsJudgedByItsFields()
    {
        Assert.Equal("""{"message":{"value":"(5 chars)","n":3}}""", R("""{"message":{"value":"hello","n":3}}"""));
        Assert.Equal("""{"body":{"To":"...0001","Text":"(2 chars)","priority":"high"}}""", R("""{"body":{"To":"+15550000001","Text":"hi","priority":"high"}}"""));
        Assert.Equal("""{"body":"(4 chars)"}""", R("""{"body":"text"}"""));
        Assert.Equal("""{"attachments":[{"name":"(5 chars)"}]}""", R("""{"attachments":[{"name":"a.pdf"}]}"""));
    }

    [Theory]
    [InlineData("+15550000001", "...0001")]
    [InlineData("+44 20 7946 0958", "...0958")]
    [InlineData("whatsapp:+15550000001", "whatsapp:...0001")]
    [InlineData("(555) 123-4567", "...4567")]
    public void PhoneNumbers_KeepTheirLast4Digits(string phone, string kept) =>
        Assert.Equal($$"""{"To":"{{kept}}"}""", R($$"""{"To":"{{phone}}"}"""));

    [Theory]
    [InlineData("2026-11-20")]
    [InlineData("2026-11-20T09:00:00Z")]
    [InlineData("12345678")]
    [InlineData("31:1")]
    [InlineData("INBOX")]
    [InlineData("pat@example.test")]
    public void PlainValues_Stay(string value) =>
        Assert.Equal($$"""{"x":"{{value}}"}""", R($$"""{"x":"{{value}}"}"""));

    [Fact]
    public void NumbersAndFlagsStay_LongOrMultiLineTextKeepsItsLength_AndLongListsAreCut()
    {
        Assert.Equal("""{"max":10,"unreadOnly":true,"after":null}""", R("""{"max":10,"unreadOnly":true,"after":null}"""));
        var longText = new string('a', CallArgsRedaction.MaxValue + 1);
        Assert.Equal($$"""{"note2":"({{longText.Length}} chars)","x":"(3 chars)"}""", R($$"""{"note2":"{{longText}}","x":"a\nb"}"""));
        var many = new JsonObject { ["ids"] = new JsonArray(Enumerable.Range(0, 25).Select(i => (JsonNode)i).ToArray()) };
        var ids = CallArgsRedaction.Redact(many)["ids"]!.AsArray();
        Assert.Equal(21, ids.Count);
        Assert.Equal("(+5 more)", (string?)ids[20]);
    }

    [Fact]
    public void ForLog_AppliesToApisAndEmailOnly_AndLeavesTheOtherArgsAsTheyAre()
    {
        string[] args = ["CreateMessage", """{"Body":"secret words"}""", "script run 4"];
        Assert.Equal(["CreateMessage", """{"Body":"(12 chars)"}""", "script run 4"], CallArgsRedaction.ForLog("api:twilio", args));
        Assert.Equal("""{"Body":"(12 chars)"}""", CallArgsRedaction.ForLog("email:Sales inbox", args)[1]);
        Assert.Same(args, CallArgsRedaction.ForLog("gh", args)); // a CLI's argv stays (CliRunner.Redact still scrubs it)
        Assert.Same(args, CallArgsRedaction.ForLog("mcp:fake", args));
        string[] notJson = ["CreateMessage", "{\"cut by the scrubber"];
        Assert.Same(notJson, CallArgsRedaction.ForLog("api:twilio", notJson));
    }
}
