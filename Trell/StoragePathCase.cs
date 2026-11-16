using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Trell;

/// <summary>
/// Finds storage directories and files whose names on disk are not lowercase. Trell lowercases
/// storage paths, so it never creates such names itself, but something else may. Windows would
/// still open "Lib" when Trell asks for "lib", while Linux would not, so a storage folder that
/// works on Windows could fail, or grow a second directory, after a move to Linux.
/// </summary>
static partial class StoragePathCase {
    /// <summary>
    /// Walks <paramref name="segments"/> below <paramref name="root"/> for as long as the
    /// entries exist. Returns true when an existing entry matches its segment only by ignoring
    /// case, and gives the path to that entry as it is on disk, relative to the root and
    /// separated by '/'. Returns false when every existing entry matches exactly. The root
    /// itself is not checked.
    /// </summary>
    public static bool TryFindCaseMismatch(
        string root,
        IReadOnlyList<string> segments,
        [NotNullWhen(true)] out string? pathOnDisk
    ) {
        pathOnDisk = null;
        var current = root;
        var walked = new List<string>();
        foreach (var segment in segments) {
            if (segment == ".") {
                continue;
            }
            var name = FindEntryName(current, segment);
            if (name is null) {
                return false;
            }
            walked.Add(name);
            if (name != segment) {
                pathOnDisk = string.Join('/', walked);
                return true;
            }
            current = Path.Join(current, name);
        }
        return false;
    }

    /// <summary>
    /// Returns the name on disk of the entry in <paramref name="dir"/> that matches
    /// <paramref name="name"/>, preferring an exact match, or null when there is none. It looks
    /// up the one name rather than listing the directory where the OS allows it, because
    /// storage directories such as "users" can hold many entries.
    /// </summary>
    static string? FindEntryName(string dir, string name) {
        var path = Path.Join(dir, name);
        if (OperatingSystem.IsWindows()) {
            if (TryFindNameOnWindows(path, out var found)) {
                return found;
            }
        } else if (OperatingSystem.IsLinux() && Path.Exists(path)) {
            // Linux file systems are case-sensitive, so the exact name exists on disk. Only a
            // missing name needs a search for one that differs in case.
            return name;
        }
        return SearchDirectory(dir, name);
    }

    static string? SearchDirectory(string dir, string name) {
        if (!Directory.Exists(dir)) {
            return null;
        }
        string? match = null;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir)) {
            var entryName = Path.GetFileName(entry);
            if (string.Equals(entryName, name, StringComparison.Ordinal)) {
                return entryName;
            }
            if (match is null && string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase)) {
                match = entryName;
            }
        }
        return match;
    }

    const int ERROR_FILE_NOT_FOUND = 2;
    const int ERROR_PATH_NOT_FOUND = 3;
    const int MAX_PATH = 260;

    /// <summary>
    /// Asks Windows for the name on disk of the last segment of <paramref name="path"/>, which
    /// Windows matches without regard to case. Returns false when it cannot tell, so the caller
    /// lists the directory instead. That happens for paths that may be too long for the call.
    /// </summary>
    static bool TryFindNameOnWindows(string path, out string? name) {
        name = null;
        if (path.Length >= MAX_PATH || path.Contains('*') || path.Contains('?')) {
            return false;
        }
        var handle = FindFirstFileExW(
            path,
            FINDEX_INFO_LEVELS.FindExInfoBasic,
            out var data,
            FINDEX_SEARCH_OPS.FindExSearchNameMatch,
            IntPtr.Zero,
            0
        );
        if (handle == new IntPtr(-1)) {
            var error = Marshal.GetLastPInvokeError();
            return error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND;
        }
        FindClose(handle);
        name = data.FileName;
        return true;
    }

    enum FINDEX_INFO_LEVELS {
        FindExInfoStandard = 0,
        FindExInfoBasic = 1,
    }

    enum FINDEX_SEARCH_OPS {
        FindExSearchNameMatch = 0,
    }

    // https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ns-minwinbase-win32_find_dataw
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct WIN32_FIND_DATAW {
        public uint dwFileAttributes;
        public uint ftCreationTimeLow;
        public uint ftCreationTimeHigh;
        public uint ftLastAccessTimeLow;
        public uint ftLastAccessTimeHigh;
        public uint ftLastWriteTimeLow;
        public uint ftLastWriteTimeHigh;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        public fixed char cFileName[MAX_PATH];
        public fixed char cAlternateFileName[14];

        public string FileName {
            get {
                fixed (char* p = cFileName) {
                    return new string(p);
                }
            }
        }
    }

    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirstfileexw
    [LibraryImport(
        "kernel32.dll",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    private static partial IntPtr FindFirstFileExW(
        string lpFileName,
        FINDEX_INFO_LEVELS fInfoLevelId,
        out WIN32_FIND_DATAW lpFindFileData,
        FINDEX_SEARCH_OPS fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags
    );

    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findclose
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(IntPtr hFindFile);
}
