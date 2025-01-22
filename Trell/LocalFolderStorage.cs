using System.Diagnostics.CodeAnalysis;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Engine.Utility.IO;

namespace Trell;

public class LocalFolderStorage : IStorageProvider {
    public int MaxDatabasePageCount { get; }

    public DirectoryInfo RootDirectory { get; }

    public LocalFolderStorage(AbsolutePath rootPath, int maxDatabasePageCount) {
        var d = new DirectoryInfo(rootPath);
        if (!d.Exists) {
            d.Create();
            if (!d.Exists) {
                throw new ArgumentException("Directory must exist and could not be created.");
            }
        }
        this.RootDirectory = d;
        this.MaxDatabasePageCount = maxDatabasePageCount;
    }

    public bool TryResolvePath(
        string path,
        [NotNullWhen(true)] out AbsolutePath resolvedPath,
        [NotNullWhen(false)] out TrellError? error
    ) {
        resolvedPath = default;
        error = null;

        if (path == ".") {
            resolvedPath = new AbsolutePath(this.RootDirectory.FullName);
            return true;
        }

        if (!TrellPath.TryParseRelative(path, out var trellPath)) {
            error = new TrellError(TrellErrorCode.INVALID_PATH, path);
            return false;
        }

        var relPath = string.Join(Path.AltDirectorySeparatorChar, trellPath.PathSegments);
        resolvedPath = new AbsolutePath(Path.Join(this.RootDirectory.FullName, relPath));
        return true;
    }

    public bool TryWithRoot(string path, [NotNullWhen(true)] out IStorageProvider? newStorage, [NotNullWhen(false)] out TrellError? error) {
        newStorage = null;
        error = null;

        if (TryResolvePath(path, out var resolvedPath, out var resolveError)) {
            if (Directory.Exists(resolvedPath)) {
                newStorage = new LocalFolderStorage(resolvedPath, this.MaxDatabasePageCount);
                return true;
            }

            if (File.Exists(resolvedPath)) {
                error = new TrellError(TrellErrorCode.PERMISSION_ERROR, "Path points to existing non-directory file");
                return false;
            }

            Directory.CreateDirectory(resolvedPath);
            newStorage = new LocalFolderStorage(resolvedPath, this.MaxDatabasePageCount);
            return true;
        }

        error = resolveError;
        return false;
    }
}
