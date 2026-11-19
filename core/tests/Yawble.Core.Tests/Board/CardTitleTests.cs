using Yawble.Core.Board;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>A card's title from an instruction: its first line, cut at 80 characters with one real ellipsis (U+2026).</summary>
public class CardTitleTests
{
    [Fact]
    public void ALongFirstLine_IsCutAt80_WithOneEllipsisCharacter()
    {
        var title = BoardProjector.Title(new string('x', 120) + "\nsecond line");
        Assert.Equal(80, title.Length);
        Assert.Equal('…', title[^1]);
        Assert.Equal(new string('x', 79), title[..79]);
    }

    [Fact]
    public void AShortFirstLine_IsKept_WithoutMarkdownMarkers() =>
        Assert.Equal("Fix the board", BoardProjector.Title("\n## Fix the board\nmore"));
}
