using System.Diagnostics.CodeAnalysis;

namespace Trell.Engine.Utility.IO;

/// <summary>
/// Makes code file names match the files on disk exactly on every OS. Windows and macOS find a
/// file whose name differs only in case, but Linux does not, so a worker that relied on that
/// would run on one OS and fail on another.
/// </summary>
public static class CodeFileCase {
    /// <summary>
    /// Compares each segment of <paramref name="path"/> below <paramref name="root"/> with the
    /// real directory entry, using ordinal comparison. Returns true when some segment matches an
    /// entry only by ignoring case, and gives the path that does exist, relative to the root and
    /// separated by '/'. Returns false when every segment matches exactly, or when some segment
    /// has no entry at all. The root itself is not checked.
    /// </summary>
    public static bool TryFindCaseMismatch(
        string root,
        string path,
        [NotNullWhen(true)] out string? pathOnDisk
    ) {
        pathOnDisk = null;
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || Path.IsPathFullyQualified(relative)) {
            return false;
        }

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,],
            StringSplitOptions.RemoveEmptyEntries
        );
        var current = Path.GetFullPath(root);
        var mismatch = false;
        for (int i = 0; i < segments.Length; i++) {
            var segment = segments[i];
            if (segment == "..") {
                return false;
            }
            if (!Directory.Exists(current)) {
                return false;
            }
            string? match = null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(current)) {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, segment, StringComparison.Ordinal)) {
                    match = name;
                    break;
                }
                if (match is null && string.Equals(name, segment, StringComparison.OrdinalIgnoreCase)) {
                    match = name;
                }
            }
            if (match is null) {
                return false;
            }
            if (match != segment) {
                mismatch = true;
                segments[i] = match;
            }
            current = Path.Join(current, match);
        }

        if (mismatch) {
            pathOnDisk = string.Join('/', segments);
        }
        return mismatch;
    }
}
