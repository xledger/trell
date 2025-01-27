using System.Diagnostics.CodeAnalysis;
using Trell.Engine.Utility.IO;

namespace Trell.Engine.Extensibility.Interfaces;

public interface IStorageProvider {
    int MaxDatabasePageCount { get; }

    bool TryResolveTrellPath(
        string path,
        [NotNullWhen(true)] out AbsolutePath resolvedPath,
        [NotNullWhen(false)] out TrellError? error);

    bool TryScopeToSubdirectory(
        string path,
        [NotNullWhen(true)] out IStorageProvider? newStorage,
        [NotNullWhen(false)] out TrellError? error);
}
