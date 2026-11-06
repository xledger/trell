using System.Linq;
using System.Text.RegularExpressions;

namespace Trell.Test;

public class BundledSkillTest : IDisposable {
    readonly string tempDir = Directory.CreateTempSubdirectory("trell_skill_test_").FullName;

    public void Dispose() {
        Directory.Delete(this.tempDir, true);
        GC.SuppressFinalize(this);
    }

    static string ReadResource(BundledSkill.SkillFile file) {
        using var reader = new StreamReader(BundledSkill.Open(file));
        return reader.ReadToEnd();
    }

    [Fact]
    public void EmbedsSkillAndReferences() {
        var paths = BundledSkill.Files.Select(f => f.RelativePath).ToList();
        Assert.Contains("SKILL.md", paths);
        Assert.Contains("references/runtime-api.md", paths);
        Assert.Contains("references/hosting.md", paths);
        Assert.All(paths, p => Assert.DoesNotContain('\\', p));
    }

    [Fact]
    public void FrontmatterNameMatchesSkillDirectory() {
        var skillMd = ReadResource(BundledSkill.Files.Single(f => f.RelativePath == "SKILL.md"));
        Assert.StartsWith("---", skillMd);
        var name = Regex.Match(skillMd, @"^name:\s*(\S+)\s*$", RegexOptions.Multiline).Groups[1].Value;
        Assert.Equal(BundledSkill.Name, name);
        Assert.Matches(new Regex(@"^description:\s*\S", RegexOptions.Multiline), skillMd);
    }

    [Fact]
    public void SkillMdLinksOnlyToBundledFiles() {
        var skillMd = ReadResource(BundledSkill.Files.Single(f => f.RelativePath == "SKILL.md"));
        var paths = BundledSkill.Files.Select(f => f.RelativePath).ToHashSet();
        foreach (Match m in Regex.Matches(skillMd, @"`(references/[^`]+)`")) {
            Assert.Contains(m.Groups[1].Value, paths);
        }
    }

    [Fact]
    public void InstallWritesEveryFileWithIdenticalContent() {
        var skillsDir = Path.Combine(this.tempDir, "skills");

        var installedAt = BundledSkill.InstallInto(skillsDir);

        Assert.Equal(Path.Combine(skillsDir, BundledSkill.Name), installedAt);
        Assert.True(BundledSkill.IsInstalledIn(skillsDir));
        foreach (var file in BundledSkill.Files) {
            var written = File.ReadAllText(Path.Combine(installedAt, file.RelativePath));
            Assert.Equal(ReadResource(file), written);
        }
    }

    [Fact]
    public void ReinstallOverwritesSkillFilesAndKeepsOtherFiles() {
        var skillsDir = Path.Combine(this.tempDir, "skills");
        var installedAt = BundledSkill.InstallInto(skillsDir);
        var skillMdPath = Path.Combine(installedAt, "SKILL.md");
        var userFile = Path.Combine(installedAt, "notes.md");
        File.WriteAllText(skillMdPath, "stale");
        File.WriteAllText(userFile, "mine");

        BundledSkill.InstallInto(skillsDir);

        Assert.NotEqual("stale", File.ReadAllText(skillMdPath));
        Assert.Equal("mine", File.ReadAllText(userFile));
    }

    [Fact]
    public void KnownTargetsUseHomeAndCurrentDirectory() {
        var home = Path.Combine(this.tempDir, "home");
        var cwd = Path.Combine(this.tempDir, "project");

        var targets = SkillTargets.Known(cwd, home, _ => null).ToDictionary(t => t.Key, t => t.SkillsDirectory);

        Assert.Equal(Path.Combine(home, ".claude", "skills"), targets["claude"]);
        Assert.Equal(Path.Combine(cwd, ".claude", "skills"), targets["claude-project"]);
        Assert.Equal(Path.Combine(home, ".codex", "skills"), targets["codex"]);
        Assert.Equal(Path.Combine(home, ".agents", "skills"), targets["agents"]);
        Assert.Equal(Path.Combine(cwd, ".agents", "skills"), targets["agents-project"]);
    }

    [Fact]
    public void KnownTargetsHonorConfigDirectoryVariables() {
        var env = new Dictionary<string, string> {
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(this.tempDir, "claude-config"),
            ["CODEX_HOME"] = Path.Combine(this.tempDir, "codex-home"),
        };

        var targets = SkillTargets.Known(this.tempDir, this.tempDir, k => env.GetValueOrDefault(k))
            .ToDictionary(t => t.Key, t => t.SkillsDirectory);

        Assert.Equal(Path.Combine(env["CLAUDE_CONFIG_DIR"], "skills"), targets["claude"]);
        Assert.Equal(Path.Combine(env["CODEX_HOME"], "skills"), targets["codex"]);
    }

    [Theory]
    [InlineData("~", "")]
    [InlineData("~/skills", "skills")]
    [InlineData("~\\skills", "skills")]
    public void ResolveUserPathExpandsHome(string input, string expectedUnderHome) {
        var home = Path.Combine(this.tempDir, "home");
        Assert.Equal(Path.GetFullPath(Path.Combine(home, expectedUnderHome)), SkillTargets.ResolveUserPath(input, home));
    }

    [Fact]
    public void ResolveUserPathMakesRelativePathsAbsolute() {
        Assert.Equal(Path.GetFullPath("some/skills"), SkillTargets.ResolveUserPath("some/skills", this.tempDir));
    }
}
