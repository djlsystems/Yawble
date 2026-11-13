using Yawble.Core.Credentials;
using Yawble.Core.Sandboxing;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Policy;
using Xunit;
using TargetOs = Yawble.Core.Policy.TargetOs;

namespace Yawble.Core.Tests.Teams;

/// <summary>A team's documents: what the person browses and edits, and what the team's runs may write.</summary>
public class TeamDocumentsTests
{
    private static (TempDb Db, TeamDocuments Docs) Make()
    {
        var db = new TempDb();
        return (db, new TeamDocuments(new TeamRepos(db.Dir, db.ConnectionString)));
    }

    [Fact]
    public void ThePersonWritesReadsListsRenamesAndDeletes_InsideTheTeamsFolder()
    {
        var (db, docs) = Make();
        using (db)
        {
            docs.MakeFolder("Alpha", null, "specs");
            docs.Write("Alpha", "specs/login.md", "# Login\n\nPasswords are hashed.");
            docs.Write("Alpha", "notes.txt", "remember the cache");
            var upload = docs.Upload("Alpha", "specs", "diagram.png", Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]), replace: false);
            var again = docs.Upload("Alpha", "specs", "diagram.png", Convert.ToBase64String([1, 2, 3]), replace: false);
            Assert.Equal("specs/diagram.png", upload["path"]!.GetValue<string>());
            Assert.Equal("specs/diagram (2).png", again["path"]!.GetValue<string>());

            var top = docs.List("Alpha", null)["entries"]!.AsArray().Select(e => (e!["name"]!.GetValue<string>(), e["folder"]!.GetValue<bool>())).ToArray();
            Assert.Equal([("specs", true), ("notes.txt", false)], top); // folders first

            var md = docs.Read("Alpha", "specs/login.md");
            Assert.Equal(("markdown", "# Login\n\nPasswords are hashed."), (md["kind"]!.GetValue<string>(), md["text"]!.GetValue<string>()));
            Assert.Equal("image", docs.Read("Alpha", "specs/diagram.png")["kind"]!.GetValue<string>());
            Assert.Equal("text", docs.Read("Alpha", "notes.txt")["kind"]!.GetValue<string>());

            docs.Rename("Alpha", "notes.txt", "todo.txt");
            Assert.Throws<DocumentsException>(() => docs.Rename("Alpha", "todo.txt", "specs"));
            docs.Delete("Alpha", "specs");
            Assert.Equal(["todo.txt"], docs.List("Alpha", null)["entries"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()));
            Assert.Throws<DocumentsException>(() => docs.Delete("Alpha", ""));
        }
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("specs/../../escape.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("..")]
    public void APathThatWouldLeaveTheFolderIsRefused(string path)
    {
        var (db, docs) = Make();
        using (db)
        {
            var e = Assert.Throws<DocumentsException>(() => docs.Write("Alpha", path, "x"));
            Assert.Equal("bad_path", e.Code);
        }
    }

    [Fact]
    public void ALinkInsideTheFolderIsNotFollowed()
    {
        var (db, docs) = Make();
        using (db)
        {
            var root = docs.Root("Alpha");
            var outside = Directory.CreateTempSubdirectory("yawble-outside-").FullName;
            try
            {
                try { Directory.CreateSymbolicLink(Path.Combine(root, "elsewhere"), outside); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // no symlink right here
                Assert.Equal("bad_path", Assert.Throws<DocumentsException>(() => docs.Write("Alpha", "elsewhere/x.txt", "x")).Code);
                Assert.DoesNotContain(docs.List("Alpha", null)["entries"]!.AsArray(), e => e!["name"]!.GetValue<string>() == "elsewhere");
            }
            finally
            {
                Directory.Delete(outside, true);
            }
        }
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("what?")]
    [InlineData("..")]
    [InlineData("")]
    public void ANameWithAPathOrAReservedCharacterIsRefused(string name)
    {
        var (db, docs) = Make();
        using (db) Assert.Equal("bad_name", Assert.Throws<DocumentsException>(() => docs.MakeFolder("Alpha", null, name)).Code);
    }

    [Fact]
    public void ARunGetsItsOwnTeamsDocuments_ReadWrite_AndNoOtherFolder()
    {
        var run = PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeSubscription);
        var mine = run with { Layout = run.Layout with { TeamDocuments = run.Layout.TeamDocumentsCandidate } };
        var compiled = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, mine);
        Assert.Empty(SandboxRequestInvariants.Check(compiled, mine));
        var rw = compiled.Request["filesystem"]!["readwritePaths"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Contains(mine.Layout.TeamDocumentsCandidate, rw);
        var env = compiled.Request["process"]!["env"]!.AsArray().Select(e => e!.GetValue<string>());
        Assert.Contains($"YAWBLE_TEAM_DOCS={mine.Layout.TeamDocumentsCandidate}", env);

        // Pointed anywhere else (another team's documents), the compiler refuses the request.
        var other = run with { Layout = run.Layout with { TeamDocuments = run.Layout.TeamDocumentsCandidate.Replace("/documents", "-other/documents") } };
        var refused = Assert.Throws<InvalidOperationException>(() => SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, other));
        Assert.Contains("not this member's team's", refused.Message);
    }
}
