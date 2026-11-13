using Xunit;

namespace Yawble.CodeBrowser.Tests;

public class FileKindsTests
{
    [Theory]
    [InlineData("a.md", "markdown")]
    [InlineData("a.png", "image")]
    [InlineData("logo.SVG", "image")]
    [InlineData("a.json", "json")]
    [InlineData("a.yml", "yaml")]
    [InlineData("a.csproj", "xml")]
    [InlineData("a.cs", "text")]
    [InlineData("Makefile", "text")]
    public void TypesComeFromTheName(string name, string type) => Assert.Equal(type, FileKinds.TypeOf(name));

    [Theory]
    [InlineData("a.cs", "csharp")]
    [InlineData("a.vue", "xml")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData("a.unknownext", null)]
    public void LanguagesAreHighlightJsNames(string name, string? language) => Assert.Equal(language, FileKinds.LanguageOf(name));

    [Fact]
    public void ANulByteIsBinaryUnlessUtf16()
    {
        Assert.True(FileKinds.LooksBinary([0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]));
        Assert.False(FileKinds.LooksBinary("plain text\n"u8));
        Assert.False(FileKinds.LooksBinary([0xFF, 0xFE, (byte)'a', 0x00]));
        Assert.Equal("a", FileKinds.Decode([0xFF, 0xFE, (byte)'a', 0x00]));
        Assert.Equal("é", FileKinds.Decode([0xEF, 0xBB, 0xBF, 0xC3, 0xA9]));
    }

    [Fact]
    public void LinesAreCountedAsAnEditorDoes()
    {
        Assert.Equal(["a", "b"], FileKinds.Lines("a\r\nb\n"));
        Assert.Equal(["a", "", "b"], FileKinds.Lines("a\n\nb"));
        Assert.Empty(FileKinds.Lines(""));
        Assert.Equal(2, FileKinds.CountLines("a\nb\n"u8));
        Assert.Equal(3, FileKinds.CountLines("a\nb\nc"u8));
    }
}

public class LineDiffTests
{
    private static string[] L(string s) => s.Length == 0 ? [] : s.Split(',');

    [Fact]
    public void AddedLines()
    {
        var h = Assert.Single(LineDiff.Compute(L("a,b,c"), L("a,b,x,y,c")));
        Assert.Equal(new DiffHunk(2, 0, 2, 2), h);
    }

    [Fact]
    public void ChangedLines()
    {
        var h = Assert.Single(LineDiff.Compute(L("a,b,c"), L("a,B,c")));
        Assert.Equal(new DiffHunk(1, 1, 1, 1), h);
    }

    [Fact]
    public void RemovedLines()
    {
        var h = Assert.Single(LineDiff.Compute(L("a,b,c,d"), L("a,d")));
        Assert.Equal(new DiffHunk(1, 2, 1, 0), h);
    }

    [Fact]
    public void SeveralHunks()
    {
        var hunks = LineDiff.Compute(L("a,b,c,d,e,f"), L("x,a,b,d,e,F"));
        Assert.Equal([new DiffHunk(0, 0, 0, 1), new DiffHunk(2, 1, 3, 0), new DiffHunk(5, 1, 5, 1)], hunks);
    }

    [Fact]
    public void NoChangeNoHunks() => Assert.Empty(LineDiff.Compute(L("a,b"), L("a,b")));

    [Fact]
    public void PastTheEditLimitTheMiddleIsOneBlock()
    {
        var a = Enumerable.Range(0, 100).Select(i => "a" + i).ToArray();
        var b = Enumerable.Range(0, 100).Select(i => "b" + i).ToArray();
        Assert.Equal(new DiffHunk(0, 100, 0, 100), Assert.Single(LineDiff.Compute(a, b, maxEdits: 10)));
    }

    [Fact]
    public void ApplyingTheHunksGivesTheNewText()
    {
        var rng = new Random(11);
        for (var round = 0; round < 300; round++)
        {
            var a = Enumerable.Range(0, rng.Next(0, 30)).Select(_ => ((char)('a' + rng.Next(5))).ToString()).ToArray();
            var b = Enumerable.Range(0, rng.Next(0, 30)).Select(_ => ((char)('a' + rng.Next(5))).ToString()).ToArray();
            var hunks = LineDiff.Compute(a, b);
            var result = new List<string>();
            var oi = 0;
            foreach (var h in hunks)
            {
                Assert.True(h.OldStart >= oi);
                result.AddRange(a[oi..h.OldStart]);
                result.AddRange(b[h.NewStart..(h.NewStart + h.NewCount)]);
                Assert.Equal(result.Count, h.NewStart + h.NewCount);
                oi = h.OldStart + h.OldCount;
            }
            result.AddRange(a[oi..]);
            Assert.Equal(b, result);
        }
    }
}
