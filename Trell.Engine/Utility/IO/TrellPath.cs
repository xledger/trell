using System.Diagnostics.CodeAnalysis;

namespace Trell.Engine.Utility.IO;

public class TrellPath {
    static readonly HashSet<char> AllowedPathCharacter = new HashSet<char>();
    const int MAX_PATH_LENGTH = 4 * 1024;
    internal static readonly TrellPath WorkerJs;

    static TrellPath() {
        for (int i = 'a'; i <= 'z'; i++) {
            AllowedPathCharacter.Add((char)i);
        }

        for (int i = 'A'; i <= 'Z'; i++) {
            AllowedPathCharacter.Add((char)i);
        }

        for (int i = '0'; i <= '9'; i++) {
            AllowedPathCharacter.Add((char)i);
        }

        AllowedPathCharacter.Add('_');
        AllowedPathCharacter.Add('-');
        AllowedPathCharacter.Add('/');
        AllowedPathCharacter.Add('.');

        WorkerJs = new(true, ["worker.js"]);
    }

    public bool Relative { get; }
    public IReadOnlyList<string> PathSegments { get; }

    TrellPath(bool relative, string[] pathSegments) {
        Relative = relative;
        PathSegments = pathSegments;
    }

    /// <summary>
    /// Parses a relative path to data that Trell names, such as a storage directory or a
    /// database file. The path may contain only letters a-z and A-Z, digits, '_', '-', '.',
    /// and '/', and may not climb out of its directory. Trell lowercases it, so names that
    /// differ only in case refer to the same file on every OS.
    /// </summary>
    public static bool TryParseRelative(
        string path,
        [NotNullWhen(true)] out TrellPath? trellPath
    ) => TryParseRelative(path, lowercase: true, out trellPath);

    /// <summary>
    /// Parses a relative path to a code file that the worker's author named, such as the worker
    /// file. The path keeps its case, because it must match the file on disk exactly. The rules
    /// are otherwise the same as <see cref="TryParseRelative(string, out TrellPath?)"/>.
    /// </summary>
    public static bool TryParseRelativeKeepingCase(
        string path,
        [NotNullWhen(true)] out TrellPath? trellPath
    ) => TryParseRelative(path, lowercase: false, out trellPath);

    static bool TryParseRelative(
        string path,
        bool lowercase,
        [NotNullWhen(true)] out TrellPath? trellPath
    ) {
        trellPath = default;
        if (string.IsNullOrWhiteSpace(path) || path.Length > MAX_PATH_LENGTH) {
            return false;
        }

        if (lowercase) {
            path = path.ToLowerInvariant();
        }

        for (int i = 0; i < path.Length; i++) {
            if (i == 0 && path[i] == '/') {
                return false;
            }
            if (!AllowedPathCharacter.Contains(path[i])) {
                return false;
            }
        }

        var pathSegments = path.Split('/');
        var removeLastSegment = false;

        for (int i = 0; i < pathSegments.Length; i++) {
            var pathSegment = pathSegments[i];
            if (pathSegment.Length == 0) {
                if (i == pathSegments.Length - 1) {
                    removeLastSegment = true;
                    continue;
                }
                return false;
            }
            for (int j = 0; j < pathSegment.Length; j++) {
                var c = pathSegment[j];
                var isDot = c == '.';
                if (isDot) {
                    if (j > 0 && pathSegment[j - 1] == '.') {
                        return false;
                    }
                    if ((j == 0 || j == pathSegment.Length - 1) && pathSegment.Length > 1) {
                        return false;
                    }
                }
            }
        }

        if (removeLastSegment) {
            pathSegments = pathSegments[0..^1];
        }

        trellPath = new TrellPath(true, pathSegments);

        return true;
    }

    public override string ToString() => string.Join('/', PathSegments);
}
