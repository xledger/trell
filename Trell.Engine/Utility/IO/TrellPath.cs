using System.Diagnostics.CodeAnalysis;

namespace Trell.Engine.Utility.IO;

public class TrellPath {
    //static readonly Regex RelativePathNoTraversalPattern =
    //    new Regex(@"\A([a-zA-Z0-9-_]+/)+([a-zA-Z0-9-_]+/?)\z",
    //        RegexOptions.ExplicitCapture
    //        | RegexOptions.Compiled,
    //        TimeSpan.FromMilliseconds(100));

    readonly static HashSet<char> AllowedPathCharacter = new HashSet<char>();
    const int MAX_PATH_LENGTH = 4 * 1024;

    static TrellPath() {
        for (int i = 'a'; i <= 'z'; i++) {
            AllowedPathCharacter.Add((char)i);
        }

        for (int i = '0'; i <= '9'; i++) {
            AllowedPathCharacter.Add((char)i);
        }

        AllowedPathCharacter.Add('_');
        AllowedPathCharacter.Add('/');
        AllowedPathCharacter.Add('.');
    }

    public bool Relative { get; }
    public IReadOnlyList<string> PathSegments { get; }

    TrellPath(bool relative, string[] pathSegments) {
        this.Relative = relative;
        this.PathSegments = pathSegments;
    }

    static bool IsSpecialPathCharacter(char c) =>
        c == '.' || c == '/';

    /// <summary>
    /// Parses a relative path without traversal.
    /// </summary>
    public static bool TryParseRelative(string path, [NotNullWhen(true)] out TrellPath? trellPath) {
        trellPath = default;
        if (string.IsNullOrWhiteSpace(path) || path.Length > MAX_PATH_LENGTH) {
            return false;
        }

        path = path.ToLowerInvariant();

        for (int i = 0; i < path.Length; i++) {
            if (i == 0 && IsSpecialPathCharacter(path[i])) {
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
                    if (j == 0) {
                        return false;
                    }
                    if (i == pathSegment.Length - 1) {
                        return false;
                    }
                    if (j > 0 && pathSegment[j - 1] == '.') {
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
}
