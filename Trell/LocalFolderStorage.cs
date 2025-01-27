using System.Diagnostics.CodeAnalysis;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Engine.Utility.IO;

namespace Trell;

public class LocalFolderStorage : IStorageProvider {
    public int MaxDatabasePageCount { get; }
    public string RootDirectory { get; }

    public LocalFolderStorage(AbsolutePath rootPath, int maxDatabasePageCount) {
        var d = new DirectoryInfo(rootPath);
        if (!d.Exists) {
            d.Create();
            if (!d.Exists) {
                throw new ArgumentException("Directory must exist and could not be created.");
            }
        }
        this.RootDirectory = d.FullName;
        this.MaxDatabasePageCount = maxDatabasePageCount;
    }

    public bool TryResolveTrellPath(
        string path,
        [NotNullWhen(true)] out AbsolutePath resolvedPath,
        [NotNullWhen(false)] out TrellError? error
    ) {
        resolvedPath = default;
        error = null;

        if (Path.IsPathFullyQualified(path)) {
            error = new TrellError(TrellErrorCode.INVALID_PATH, path);
            return false;
        }

        if (!TrellPath.TryParseRelative(path, out var trellPath)) {
            error = new TrellError(TrellErrorCode.INVALID_PATH, path);
            return false;
        }
        var relPath = string.Join(Path.DirectorySeparatorChar, trellPath.PathSegments);
        resolvedPath = new AbsolutePath(Path.Join(this.RootDirectory, relPath));

        return true;
    }

    public bool TryScopeToSubdirectory(
        string path,
        [NotNullWhen(true)] out IStorageProvider? newStorage,
        [NotNullWhen(false)] out TrellError? error
    ) {
        newStorage = null;
        error = null;

        if (TryResolveTrellPath(path, out var resolvedPath, out var resolveError)) {
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
