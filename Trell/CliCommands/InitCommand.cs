using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Trell.CliCommands;

public class InitCommandSettings : CommandSettings {
    [CommandOption("-d|--user-data-dir <directory>"), Description("Directory to use for trell user/worker data.")]
    public string? UserDataDirectory { get; set; }
}

class InitCommand : AsyncCommand<InitCommandSettings> {
    public async override Task<int> ExecuteAsync(CommandContext context, InitCommandSettings settings) {
        string userDataDirectory;
        if (settings.UserDataDirectory is not null) {
            userDataDirectory = Path.GetFullPath(settings.UserDataDirectory);
        } else {
            userDataDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TrellUserData"));
        }

        if (TryCreateNewFile("Trell.toml", out var trellCfgFs)) {
            Directory.CreateDirectory(userDataDirectory);
            Log.Information("Created {dir}", userDataDirectory);
            using (trellCfgFs)
            using (var sw = new StreamWriter(trellCfgFs)) {
                await sw.WriteLineAsync($"""
                [logger]
                type = "Trell.ConsoleLogger"
            
                [storage]
                path = "{new Tomlyn.Syntax.StringValueSyntax(userDataDirectory)}"
            
                [worker.pool]
                size = 10
            
                [worker.limits]
                max_startup_duration = "1s"
                max_execution_duration = "15m"
                grace_period = "10s"
                """);
            }
            Log.Information("Wrote {file}", trellCfgFs.Name);
        } else {
            Log.Information("{f} already exists. skipping.", "Trell.config");
        }

        var exampleUserName = "example-user";
        var exampleWorkerName = "example-worker";
        var exampleDir = Path.GetFullPath(Path.Combine(userDataDirectory, exampleUserName, exampleWorkerName, "src"));
        if (Directory.Exists(exampleDir)) {
            Log.Information("Example worker already exists, skipping.");
            return 0;
        }

        var shouldCreateExample =
            AnsiConsole.Prompt(
                new TextPrompt<bool>("Create Example Worker?")
                .AddChoice(true)
                .AddChoice(false)
                .DefaultValue(true)
                .WithConverter(choice => choice ? "y" : "n"));

        if (!shouldCreateExample) {
            return 0;
        }

        Directory.CreateDirectory(exampleDir);

        var workerJsPath = Path.GetFullPath("worker.js", exampleDir);

        if (!TryCreateNewFile(workerJsPath, out var workerJsFs)) {
            Log.Information("Example worker already exists, skipping.");
            return 0;
        }
        using (workerJsFs)
        using (var sw = new StreamWriter(workerJsFs)) {
            await sw.WriteLineAsync("""
                export default {
                  // Run me with `trell run dir TrellUserData/example-user/example-worker`
                  async scheduled(_event, env, _ctx) {
                    console.log('Hello World!');
                  }
                };
                """);
        }


        return 0;
    }

    bool TryCreateNewFile(string path, [NotNullWhen(true)] out FileStream? fs) {
        try {
            fs = File.Open(path, mode: FileMode.CreateNew, access: FileAccess.ReadWrite);
            return true;
        } catch (IOException) {
            fs = null;
            return false;
        }
    }
}
