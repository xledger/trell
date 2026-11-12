using Trell.Engine.Utility.IO;

namespace Trell.Test.Utility.IO;

public class TrellPathTest {
    [Fact]
    public void TestBadRelativePath() {
        Assert.False(TrellPath.TryParseRelative("/foo/bar", out _));
        Assert.False(TrellPath.TryParseRelative("//bar", out _));
        Assert.False(TrellPath.TryParseRelative("bar//", out _));
        Assert.False(TrellPath.TryParseRelative("../bar/", out _));
        Assert.False(TrellPath.TryParseRelative("bar/../foo", out _));
        Assert.False(TrellPath.TryParseRelative("bar/.foo", out _));
        Assert.False(TrellPath.TryParseRelative(".foo", out _));
        Assert.False(TrellPath.TryParseRelative("foo.", out _));
        Assert.False(TrellPath.TryParseRelative("bar/Sentence Folder Guy/foo", out _));
        Assert.False(TrellPath.TryParseRelative("bar\\WindowsMan\\foo", out _));
        Assert.False(TrellPath.TryParseRelative(" WhiteSpaceMaxxer ", out _));
    }

    [Fact]
    public void TestRelativePath() {
        Assert.True(TrellPath.TryParseRelative("CAPSLOCK/1connoisseur", out var path));
        Assert.Equal("capslock", path.PathSegments[0]);
        Assert.Equal("1connoisseur", path.PathSegments[1]);
        Assert.True(TrellPath.TryParseRelative("./bar", out _));

        Assert.True(TrellPath.TryParseRelative("foo/bar/", out path));
        Assert.Equal("foo", path.PathSegments[0]);
        Assert.Equal("bar", path.PathSegments[1]);

        Assert.True(TrellPath.TryParseRelative("foo.db", out path));
    }

    [Fact]
    public void TestRelativePathAllowsHyphens() {
        Assert.True(TrellPath.TryParseRelative("my-job.js", out var path));
        Assert.Equal("my-job.js", path.ToString());

        var guid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        Assert.True(TrellPath.TryParseRelative($"users/{guid}/workers/{guid}/src/", out path));
        Assert.Equal($"users/{guid}/workers/{guid}/src", path.ToString());

        Assert.True(TrellPath.TryParseRelative("-/a-/-b/--", out path));
        Assert.Equal(["-", "a-", "-b", "--",], path.PathSegments);

        // Paths are still lowercased.
        Assert.True(TrellPath.TryParseRelative("My-Job.js", out path));
        Assert.Equal("my-job.js", path.ToString());

        // A hyphen does not loosen the rules about dots.
        Assert.False(TrellPath.TryParseRelative("my-job/../foo", out _));
        Assert.False(TrellPath.TryParseRelative("-/.foo", out _));
    }

    [Fact]
    public void TestRelativePathKeepingCase() {
        // Code paths keep their case.
        Assert.True(TrellPath.TryParseRelativeKeepingCase("Lib/MyWorker.js", out var path));
        Assert.Equal(["Lib", "MyWorker.js",], path.PathSegments);
        Assert.Equal("Lib/MyWorker.js", path.ToString());

        // Data paths are still lowercased.
        Assert.True(TrellPath.TryParseRelative("Lib/MyWorker.js", out path));
        Assert.Equal("lib/myworker.js", path.ToString());

        // The other rules still apply.
        Assert.False(TrellPath.TryParseRelativeKeepingCase("/Foo/Bar.js", out _));
        Assert.False(TrellPath.TryParseRelativeKeepingCase("Foo/../Bar.js", out _));
        Assert.False(TrellPath.TryParseRelativeKeepingCase("Foo/.Bar", out _));
        Assert.False(TrellPath.TryParseRelativeKeepingCase("Foo\\Bar.js", out _));
        Assert.False(TrellPath.TryParseRelativeKeepingCase("My Worker.js", out _));
    }

    [Fact]
    public void TestRelativePathRegression() {
        // Covers a regression where wrong iterator variable was used to determine if
        // a '.' character in a path's segment was its last character or not.
        Assert.True(TrellPath.TryParseRelative("x/y/2.0/z", out _));
    }
}
