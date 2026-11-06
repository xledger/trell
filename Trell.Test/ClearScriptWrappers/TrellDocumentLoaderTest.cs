using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using static Trell.Engine.ClearScriptWrappers.EngineWrapper;
using static Trell.Engine.ClearScriptWrappers.EngineWrapper.TrellDocumentLoader;

namespace Trell.Test.ClearScriptWrappers;

public class TrellDocumentLoaderTest(ITestOutputHelper output) {
    static readonly string[] DIR_SUFFIXES = [
        "",
        Path.DirectorySeparatorChar.ToString(),
        Path.AltDirectorySeparatorChar.ToString(),
    ];
    static readonly string[] FILE_PREFIXES = ["", "./",];

    [Theory]
    [InlineData("/foo", "/foo", "bar")]
    [InlineData("/foo", "/foo/bax", "../bar")]
    [InlineData("/foo", "/foo/bax/zip", "../../bar")]
    [InlineData("/foo", "/foo/bax/zip", "../../../foo/bax/bar")]
    public void TestGoodPaths(string root, string dir, string file) {
        foreach (var (rootSlash, dirSlash, filePre) in Permutations(
            DIR_SUFFIXES,
            DIR_SUFFIXES,
            FILE_PREFIXES
        )) {
            var res =
                TryGetRootedPath(root + rootSlash, dir + dirSlash, filePre + file, out var path);
            output.WriteLine($"Path:  {path}");
            Assert.Equal(Path.GetFullPath(Path.Combine(dir, file)), path);
            Assert.True(res);
        }
    }

    [Theory]
    [InlineData("/foo", "/foo", "../bar")]
    [InlineData("/foo", "/", "bar")]
    [InlineData("/foo", "/bar", "zip")]
    public void TestBadPaths(string root, string dir, string file) {
        foreach (var (rootSlash, dirSlash, filePre) in Permutations(
            DIR_SUFFIXES,
            DIR_SUFFIXES,
            FILE_PREFIXES
        )) {
            Assert.False(TryGetRootedPath(root + rootSlash, dir + dirSlash, filePre + file, out _));
        }
    }

    [Fact]
    public async Task TestImportsMustMatchCaseOnDisk() {
        var root = Directory.CreateTempSubdirectory("trell_loader_test_").FullName;
        try {
            Directory.CreateDirectory(Path.Join(root, "Lib"));
            File.WriteAllText(Path.Join(root, "Utils.js"), "export const utils = 1;");
            File.WriteAllText(Path.Join(root, "Lib", "Helper.js"), "export const helper = 2;");

            var settings = new DocumentSettings {
                AccessFlags = DocumentAccessFlags.EnableFileLoading,
                SearchPath = root,
                FileNameExtensions = ".js",
            };
            var loader = new TrellDocumentLoader();
            async Task<string> Load(string specifier) {
                var doc = await loader.LoadDocumentAsync(
                    settings,
                    null,
                    specifier,
                    ModuleCategory.Standard,
                    null!
                );
                using var reader = new StreamReader(doc.Contents);
                return await reader.ReadToEndAsync();
            }
            async Task<string> LoadError(string specifier) {
                var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => Load(specifier));
                return ex.Message;
            }

            Assert.Equal("export const utils = 1;", await Load("./Utils.js"));
            Assert.Equal("export const helper = 2;", await Load("./Lib/Helper.js"));

            // The file system on Windows and macOS would find these files, so the loader must
            // reject them itself, with the same message on every OS.
            Assert.Equal(
                "\"./utils.js\" not found; the file on disk is \"Utils.js\"",
                await LoadError("./utils.js")
            );
            Assert.Equal(
                "\"./lib/Helper.js\" not found; the file on disk is \"Lib/Helper.js\"",
                await LoadError("./lib/Helper.js")
            );
            Assert.Equal(
                "\"Lib/HELPER.js\" not found; the file on disk is \"Lib/Helper.js\"",
                await LoadError("Lib/HELPER.js")
            );

            // A file that does not exist in any case gets no case message.
            Assert.DoesNotContain("on disk", await LoadError("./missing.js"));
        } finally {
            Directory.Delete(root, true);
        }
    }

    static IEnumerable<(TX, TY, TZ)> Permutations<TX, TY, TZ>(
        IEnumerable<TX> xs,
        IEnumerable<TY> ys,
        IEnumerable<TZ> zs
    ) {
        foreach (var x in xs) {
            foreach (var y in ys) {
                foreach (var z in zs) {
                    yield return (x, y, z);
                }
            }
        }
    }
}
