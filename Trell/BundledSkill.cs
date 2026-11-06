namespace Trell;

/// <summary>
/// The agent skill that ships inside the Trell executable. It teaches coding
/// agents (Claude Code, Codex, and others) how to write Trell workers.
/// </summary>
/// <remarks>
/// Trell.csproj embeds every file under Skill/ as a resource named
/// "TrellSkill/&lt;path under Skill/&gt;". MSBuild builds that name from
/// %(RecursiveDir), which uses the build machine's directory separator, so a
/// Windows build produces backslashes. <see cref="Files"/> normalizes them.
/// </remarks>
static class BundledSkill {
    public const string Name = "trell-worker";

    const string RESOURCE_PREFIX = "TrellSkill/";

    public sealed record SkillFile(string RelativePath, string ResourceName);

    /// <summary>
    /// The skill's files, with paths relative to the skill directory and '/' as the separator.
    /// </summary>
    public static IReadOnlyList<SkillFile> Files { get; } = LoadFiles();

    static IReadOnlyList<SkillFile> LoadFiles() {
        var prefix = RESOURCE_PREFIX + Name + "/";
        return typeof(BundledSkill).Assembly.GetManifestResourceNames()
            .Select(resource => (Resource: resource, Normalized: resource.Replace('\\', '/')))
            .Where(x => x.Normalized.StartsWith(prefix, StringComparison.Ordinal))
            .Select(x => new SkillFile(x.Normalized[prefix.Length..], x.Resource))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    public static Stream Open(SkillFile file) =>
        typeof(BundledSkill).Assembly.GetManifestResourceStream(file.ResourceName)
        ?? throw new InvalidOperationException($"Missing embedded resource {file.ResourceName}.");

    /// <summary>
    /// True if <paramref name="skillsDirectory"/> already holds a copy of the skill.
    /// </summary>
    public static bool IsInstalledIn(string skillsDirectory) =>
        File.Exists(Path.Combine(skillsDirectory, Name, "SKILL.md"));

    /// <summary>
    /// Writes the skill to &lt;skillsDirectory&gt;/trell-worker and returns that path.
    /// Files with the same names are overwritten; other files in the directory are left alone.
    /// </summary>
    public static string InstallInto(string skillsDirectory) {
        if (Files.Count == 0) {
            throw new InvalidOperationException("This build of Trell contains no skill files.");
        }

        var skillDirectory = Path.Combine(Path.GetFullPath(skillsDirectory), Name);
        foreach (var file in Files) {
            var destination = Path.Combine(skillDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var source = Open(file);
            using var target = File.Create(destination);
            source.CopyTo(target);
        }
        return skillDirectory;
    }
}

/// <summary>
/// A skills directory that an agent reads skills from.
/// </summary>
sealed record SkillTarget(string Key, string Description, string SkillsDirectory);

static class SkillTargets {
    /// <summary>
    /// The skills directories that <c>trell install-skill</c> offers, for the current user and directory.
    /// </summary>
    public static IReadOnlyList<SkillTarget> Known() => Known(
        Directory.GetCurrentDirectory(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable);

    public static IReadOnlyList<SkillTarget> Known(
        string currentDirectory,
        string homeDirectory,
        Func<string, string?> getEnvironmentVariable
    ) {
        // Claude Code and Codex each let the user move their config directory with an environment variable.
        var claudeHome = NonEmpty(getEnvironmentVariable("CLAUDE_CONFIG_DIR")) ?? Path.Combine(homeDirectory, ".claude");
        var codexHome = NonEmpty(getEnvironmentVariable("CODEX_HOME")) ?? Path.Combine(homeDirectory, ".codex");

        return [
            new("claude", "Claude Code, all projects", Path.Combine(claudeHome, "skills")),
            new("claude-project", "Claude Code, this project only", Path.Combine(currentDirectory, ".claude", "skills")),
            new("codex", "Codex, all projects", Path.Combine(codexHome, "skills")),
            new("agents", "Shared .agents folder, all projects", Path.Combine(homeDirectory, ".agents", "skills")),
            new("agents-project", "Shared .agents folder, this project only", Path.Combine(currentDirectory, ".agents", "skills")),
        ];

        static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>
    /// Expands a leading "~" to the home directory and makes the path absolute.
    /// Shells such as PowerShell pass "~" through to programs unexpanded.
    /// </summary>
    public static string ResolveUserPath(string path, string homeDirectory) {
        path = path.Trim();
        if (path == "~") {
            path = homeDirectory;
        } else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)) {
            path = Path.Combine(homeDirectory, path[2..]);
        }
        return Path.GetFullPath(path);
    }
}
