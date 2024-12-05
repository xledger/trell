using static Trell.Engine.ClearScriptWrappers.EngineWrapper.TrellDocumentLoader;

namespace Trell.Test.ClearScriptWrappers;

public class TrellDocumentLoaderTest {
    static readonly string[] DIR_SUFFIXES = ["", "/", "\\"];
    static readonly string[] FILE_PREFIXES = ["", "./"];

    [Theory]
    [InlineData("/foo", "/foo", "bar")]
    [InlineData("/foo", "/foo/bax", "../bar")]
    [InlineData("/foo", "/foo/bax/zip", "../../bar")]
    [InlineData("/foo", "/foo/bax/zip", "../../../foo/bax/bar")]
    public void TestGoodPaths(string root, string dir, string file) {
        foreach (var (rootSlash, dirSlash, filePre) in Permutations(DIR_SUFFIXES, DIR_SUFFIXES, FILE_PREFIXES)) {
            Assert.True(TryGetRootedPath(root + rootSlash, dir + dirSlash, filePre + file, out var path));
            Assert.Equal(Path.GetFullPath(Path.Combine(dir, file)), path);
        }
    }

    [Theory]
    [InlineData("/foo", "/foo", "../bar")]
    [InlineData("/foo", "/", "bar")]
    [InlineData("/foo", "/bar", "zip")]
    public void TestBadPaths(string root, string dir, string file) {
        foreach (var (rootSlash, dirSlash, filePre) in Permutations(DIR_SUFFIXES, DIR_SUFFIXES, FILE_PREFIXES)) {
            Assert.False(TryGetRootedPath(root + rootSlash, dir + dirSlash, filePre + file, out _));
        }
    }

    static IEnumerable<(TX, TY, TZ)> Permutations<TX, TY, TZ>(
        IEnumerable<TX> xs, IEnumerable<TY> ys, IEnumerable<TZ> zs) {
        foreach (var x in xs) {
            foreach (var y in ys) {
                foreach (var z in zs) {
                    yield return (x, y, z);
                }
            }
        }
    }
}