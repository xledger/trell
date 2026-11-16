using System.Linq;
using Trell.Engine.Extensibility;

namespace Trell.Test;

public class LocalFolderStorageTest : IDisposable {
    readonly string root;
    readonly LocalFolderStorage storage;

    public LocalFolderStorageTest() {
        root = Directory.CreateTempSubdirectory("trell_storage_test_").FullName;
        storage = new LocalFolderStorage(root, 1024);
    }

    public void Dispose() {
        Directory.Delete(root, true);
    }

    [Fact]
    public void ResolvesLowercaseEntriesAndMissingPaths() {
        Directory.CreateDirectory(Path.Join(root, "lib", "sub"));
        File.WriteAllText(Path.Join(root, "lib", "data.db"), "");

        Assert.True(storage.TryResolveTrellPath("Lib/Data.db", out var resolved, out _));
        Assert.Equal(Path.Join(root, "lib", "data.db"), resolved.ToString());
        Assert.True(storage.TryResolveTrellPath("./lib/sub/", out _, out _));
        Assert.True(storage.TryResolveTrellPath("lib/missing/new.db", out _, out _));
        Assert.True(storage.TryResolveTrellPath("missing/new.db", out _, out _));
    }

    [Fact]
    public void RefusesEntriesThatAreNotLowercaseOnDisk() {
        // Windows would open these entries when Trell asks for "lib" or "data.db", but Linux
        // would not, so storage must refuse them on every OS.
        Directory.CreateDirectory(Path.Join(root, "Lib"));
        Directory.CreateDirectory(Path.Join(root, "ok"));
        File.WriteAllText(Path.Join(root, "ok", "Data.db"), "");

        Assert.False(storage.TryResolveTrellPath("lib/x.db", out _, out var error));
        Assert.Equal(TrellErrorCode.INVALID_PATH, error.Code);
        Assert.Equal(
            "\"lib/x.db\" refers to \"lib/x.db\", but the storage folder has \"Lib\"; "
            + "Trell lowercases storage paths, so rename it in lowercase",
            error.Message
        );

        Assert.False(storage.TryResolveTrellPath("Lib", out _, out error));
        Assert.Contains("the storage folder has \"Lib\"", error.Message);

        Assert.False(storage.TryResolveTrellPath("ok/data.db", out _, out error));
        Assert.Contains("the storage folder has \"ok/Data.db\"", error.Message);

        // Scoping must not create a second, lowercase directory beside the existing one.
        Assert.False(storage.TryScopeToSubdirectory("lib/sub", out _, out _));
        Assert.Equal(["Lib", "ok",], Directory.GetDirectories(root).Select(Path.GetFileName).Order());
    }
}
