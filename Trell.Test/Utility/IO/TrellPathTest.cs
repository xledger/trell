using Trell.Engine.Utility.IO;

namespace Trell.Test.Utility.IO;

public class TrellPathTest {
    [Fact]
    public void TestBadRelativePath() {
        Assert.False(TrellPath.TryParseRelative("/foo/bar", out _));
        Assert.False(TrellPath.TryParseRelative("//bar", out _));
        Assert.False(TrellPath.TryParseRelative("bar//", out _));
        Assert.False(TrellPath.TryParseRelative("../bar/", out _));
        Assert.False(TrellPath.TryParseRelative("./bar", out _));
        Assert.False(TrellPath.TryParseRelative("bar/../foo", out _));
        Assert.False(TrellPath.TryParseRelative("bar/.foo", out _));
        Assert.False(TrellPath.TryParseRelative(".foo", out _));
        Assert.False(TrellPath.TryParseRelative("bar/Sentence Folder Guy/foo", out _));
        Assert.False(TrellPath.TryParseRelative("bar\\WindowsMan\\foo", out _));
        Assert.False(TrellPath.TryParseRelative(" WhiteSpaceMaxxer ", out _));
    }

    [Fact]
    public void TestRelativePath() {
        Assert.True(TrellPath.TryParseRelative("CAPSLOCK/1connoisseur", out var path));
        Assert.Equal("capslock", path.PathSegments[0]);
        Assert.Equal("1connoisseur", path.PathSegments[1]);

        Assert.True(TrellPath.TryParseRelative("foo/bar/", out path));
        Assert.Equal("foo", path.PathSegments[0]);
        Assert.Equal("bar", path.PathSegments[1]);

        Assert.True(TrellPath.TryParseRelative("foo.db", out path));
    }
}
