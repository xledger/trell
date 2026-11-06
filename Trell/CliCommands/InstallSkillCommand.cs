using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Trell.CliCommands;

public class InstallSkillCommandSettings : CommandSettings {
    [CommandOption("-t|--target <TARGET>")]
    [Description("Where to install: claude, claude-project, codex, agents, or agents-project. Repeat to install in several places.")]
    public string[]? Targets { get; set; }

    [CommandOption("-p|--path <DIR>")]
    [Description($"Install into another skills directory; the skill goes in <DIR>/{BundledSkill.Name}. Repeatable.")]
    public string[]? Paths { get; set; }

    [CommandOption("-f|--force")]
    [Description("Overwrite an existing copy of the skill without asking")]
    public bool Force { get; set; }

    public override ValidationResult Validate() {
        var known = SkillTargets.Known().Select(t => t.Key).ToList();
        foreach (var target in this.Targets ?? []) {
            if (!known.Contains(target, StringComparer.OrdinalIgnoreCase)) {
                return ValidationResult.Error($"Unknown target \"{target}\". Choose from: {string.Join(", ", known)}.");
            }
        }
        if ((this.Paths ?? []).Any(string.IsNullOrWhiteSpace)) {
            return ValidationResult.Error("--path needs a directory.");
        }
        return ValidationResult.Success();
    }
}

class InstallSkillCommand : Command<InstallSkillCommandSettings> {
    public override int Execute(CommandContext context, InstallSkillCommandSettings settings) {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var known = SkillTargets.Known();
        var interactive = !Console.IsInputRedirected && AnsiConsole.Profile.Capabilities.Interactive;

        var directories = new List<string>();
        foreach (var key in settings.Targets ?? []) {
            directories.Add(known.First(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)).SkillsDirectory);
        }
        foreach (var path in settings.Paths ?? []) {
            directories.Add(SkillTargets.ResolveUserPath(path, home));
        }

        if (directories.Count == 0) {
            if (!interactive) {
                AnsiConsole.MarkupLine("[red]Choose where to install the skill with --target or --path.[/]");
                WriteTargets(known);
                return 1;
            }
            directories.AddRange(AskForDirectories(known, home));
        }

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var failures = 0;
        foreach (var directory in directories.Select(Path.GetFullPath).Distinct(pathComparer)) {
            if (BundledSkill.IsInstalledIn(directory) && !settings.Force) {
                var destination = Path.Combine(directory, BundledSkill.Name);
                if (!interactive) {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]Skipped {destination}: the skill is already there. Pass --force to overwrite it.[/]");
                    continue;
                }
                if (!AnsiConsole.Confirm($"{BundledSkill.Name} is already installed in {destination.EscapeMarkup()}. Overwrite it?", false)) {
                    AnsiConsole.MarkupLineInterpolated($"Left {destination} unchanged.");
                    continue;
                }
            }

            try {
                var installedAt = BundledSkill.InstallInto(directory);
                AnsiConsole.MarkupLineInterpolated($"Installed [green]{BundledSkill.Name}[/] in {installedAt}");
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                AnsiConsole.MarkupLineInterpolated($"[red]Could not install in {directory}: {ex.Message}[/]");
                failures++;
            }
        }

        return failures == 0 ? 0 : 1;
    }

    static IEnumerable<string> AskForDirectories(IReadOnlyList<SkillTarget> known, string home) {
        var other = new SkillTarget("other", "Another directory...", "");

        var chosen = AnsiConsole.Prompt(
            new MultiSelectionPrompt<SkillTarget>()
                .Title($"Where should Trell install the [green]{BundledSkill.Name}[/] skill?")
                .InstructionsText("[grey](Press [blue]<space>[/] to choose a location, [green]<enter>[/] to install)[/]")
                .PageSize(10)
                .UseConverter(t => t == other
                    ? t.Description
                    : $"{t.Description.EscapeMarkup()} [grey]{t.SkillsDirectory.EscapeMarkup()}[/]"
                        + (BundledSkill.IsInstalledIn(t.SkillsDirectory) ? " [yellow](installed)[/]" : ""))
                .AddChoices(known)
                .AddChoices(other));

        foreach (var target in chosen) {
            if (target == other) {
                var path = AnsiConsole.Prompt(
                    new TextPrompt<string>($"Skills directory (the skill goes in a [green]{BundledSkill.Name}[/] folder inside it):")
                        .Validate(p => string.IsNullOrWhiteSpace(p)
                            ? ValidationResult.Error("Enter a directory.")
                            : ValidationResult.Success()));
                yield return SkillTargets.ResolveUserPath(path, home);
            } else {
                yield return target.SkillsDirectory;
            }
        }
    }

    static void WriteTargets(IReadOnlyList<SkillTarget> known) {
        var width = known.Max(t => t.Key.Length);
        AnsiConsole.WriteLine("Targets:");
        foreach (var t in known) {
            // Plain WriteLine, not markup: paths may contain '[' and must stay copyable.
            AnsiConsole.WriteLine($"  {t.Key.PadRight(width)}  {Path.Combine(t.SkillsDirectory, BundledSkill.Name)}");
        }
    }
}
