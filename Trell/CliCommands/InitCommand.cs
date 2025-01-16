using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Spectre.Console;
using Spectre.Console.Cli;
using Trell.Engine.Extensibility;
using static Trell.DirectoryHelper;

namespace Trell.CliCommands;

public class InitCommandSettings : CommandSettings {
    [CommandOption("-c|--config-dir <directory>"), Description("Directory to use for Trell's configuration data.")]
    public string? ConfigDirectory { get; set; }

    [CommandOption("-d|--user-data-dir <directory>"), Description("Directory to use for Trell user/worker data.")]
    public string? UserDataDirectory { get; set; }

    [CommandOption("-u|--username <username>"), Description("New username to initialize example worker for.")]
    public string? Username { get; set; }

    [CommandOption("-w|--worker-name <worker-name>"), Description("Name of new example worker to initialize.")]
    public string? WorkerName { get; set; }

    [CommandOption("--skip-example-worker"), Description("If set, no example worker will be created. Overrules -u and -w options.")]
    public bool SkipExample { get; set; }

    [CommandOption("--force"), Description("If set, any existing configurations and workers will be clobbered without asking user for permission.")]
    public bool Force { get; set; }
}

class InitCommand : AsyncCommand<InitCommandSettings> {
    public async override Task<int> ExecuteAsync(CommandContext context, InitCommandSettings settings) {
        // TODO: validate user input from all the following prompts, and possibly expand TrellPath's valid characters.
        // We use TrellPath.TryParseRelative to navigate to some of these folders, so if the names given contain
        // any characters like 'ø' it can fail TrellPath's validation for paths.

        var configDir = settings.ConfigDirectory ?? AnsiConsole.Prompt(
            new TextPrompt<string>("Please provide a path for where to store configuration data")
            .DefaultValue(Directory.GetCurrentDirectory())
            .ShowDefaultValue()
        );
        configDir = Path.GetFullPath(configDir);
        var configFilePath = Path.GetFullPath("Trell.toml", configDir);
        var configAlreadyExists = File.Exists(configFilePath);

        if (configAlreadyExists && !settings.Force) {
            var shouldClobberConfig = AnsiConsole.Prompt(
                new TextPrompt<bool>("Existing configuration found. Continuing will overwrite the existing file. Are you sure you want to continue?")
                .AddChoice(true)
                .AddChoice(false)
                .DefaultValue(false)
                .WithConverter(choice => choice ? "y" : "n")
            );

            if (!shouldClobberConfig) {
                AnsiConsole.WriteLine("Initialization terminated early, exiting...");
                return 1;
            }
        }

        var userDataRootDirectory = settings.UserDataDirectory ?? AnsiConsole.Prompt(
            new TextPrompt<string>("Please provide a path for where to store user data")
            .DefaultValue(DEFAULT_USER_DATA_ROOT_DIR)
            .ShowDefaultValue()
        );
        userDataRootDirectory = Path.GetFullPath(userDataRootDirectory);

        if (!Directory.Exists(userDataRootDirectory)) {
            Directory.CreateDirectory(userDataRootDirectory);
            AnsiConsole.WriteLine($"Created {userDataRootDirectory}");
        }

        var config = TrellConfig.CreateNew();
        config.Storage.Path = userDataRootDirectory;
        if (configAlreadyExists) {
            File.Delete(configFilePath);
        }
        using var configFs = File.Open(configFilePath, FileMode.CreateNew, FileAccess.ReadWrite);
        using var configSw = new StreamWriter(configFs);
        if (!config.TryConvertToToml(out var configAsText)) {
            throw new TrellException("Unable to convert config to TOML");
        }
        await configSw.WriteLineAsync(configAsText);

        AnsiConsole.WriteLine(configAlreadyExists ? $"Overwrote {configFilePath}" : $"Created {configFilePath}");

        var shouldSkipExample = settings.SkipExample || !AnsiConsole.Prompt(
            new TextPrompt<bool>("Would you like to create an example worker?")
            .AddChoice(true)
            .AddChoice(false)
            .DefaultValue(true)
            .WithConverter(choice => choice ? "y" : "n")
        );

        if (!shouldSkipExample) {
            var username = settings.Username ?? AnsiConsole.Prompt(
                new TextPrompt<string>("Please provide a username")
                .DefaultValue("new-user")
                .ShowDefaultValue()
            );

            var workerName = settings.WorkerName ?? AnsiConsole.Prompt(
                new TextPrompt<string>("Please provide a name for a new worker")
                .DefaultValue("new-worker")
                .ShowDefaultValue()
            );

            var workerFilePath = Path.GetFullPath("worker.js", GetWorkerSrcPath(userDataRootDirectory, username, workerName));
            var workerAlreadyExists = File.Exists(workerFilePath);

            if (workerAlreadyExists && !settings.Force) {
                var shouldClobberWorker = AnsiConsole.Prompt(
                    new TextPrompt<bool>("Existing worker found. Continuing will overwrite the existing file. Are you sure you want to continue?")
                    .AddChoice(true)
                    .AddChoice(false)
                    .DefaultValue(false)
                    .WithConverter(choice => choice ? "y" : "n")
                );

                if (!shouldClobberWorker) {
                    AnsiConsole.WriteLine("Initialization terminated early, exiting...");
                    return 1;
                }
            }

            MakeDirectoriesForNewWorker(userDataRootDirectory, username, workerName);

            if (workerAlreadyExists) {
                File.Delete(workerFilePath);
            }
            using var workerFs = File.Open(workerFilePath, FileMode.CreateNew, FileAccess.ReadWrite);
            using var workerSw = new StreamWriter(workerFs);
            await workerSw.WriteLineAsync("""
                async function scheduled(event, env, ctx) {
                    console.log("running scheduled")
                }

                function fetch(request, env, ctx) {
                    console.log("running fetch")
                }

                function upload(payload, env, ctx) {
                    console.log("running upload")
                }

                export default {
                    scheduled,
                    fetch,
                    upload,
                }
        
                """
            );

            AnsiConsole.WriteLine(workerAlreadyExists ? $"Overwrote {workerFilePath}" : $"Created {workerFilePath}");
            AnsiConsole.WriteLine($"""
                You can run the example worker commands with:
                    trell run --user-id {username} --handler scheduled worker {workerName}
                    trell run --user-id {username} --handler fetch worker {workerName}
                    trell run --user-id {username} --handler upload worker {workerName}
                """
            );
        }

        AnsiConsole.WriteLine("Trell initialization complete");

        return 0;
    }
}
