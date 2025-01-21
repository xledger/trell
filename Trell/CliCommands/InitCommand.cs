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
using Tomlyn.Syntax;
using Trell.Engine.Extensibility;
using Trell.Engine.Utility.IO;
using static Trell.DirectoryHelper;

namespace Trell.CliCommands;

public class InitCommandSettings : CommandSettings {
    [CommandOption("-c|--config-dir <directory>"), Description("Directory to use for Trell's configuration data.")]
    public string? ConfigDirectory { get; set; }

    [CommandOption("-d|--user-data-dir <directory>"), Description("Directory to use for Trell user/worker data.")]
    public string? UserDataDirectory { get; set; }

    [CommandOption("-u|--user <username>"), Description("New username to initialize example worker for.")]
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

        if (!Directory.Exists(configDir)) {
            Directory.CreateDirectory(configDir);
            AnsiConsole.WriteLine($"Created {configDir}");
        }

        if (!Directory.Exists(userDataRootDirectory)) {
            Directory.CreateDirectory(userDataRootDirectory);
            AnsiConsole.WriteLine($"Created {userDataRootDirectory}");
        }
        
        await File.WriteAllTextAsync(configFilePath, $$"""
            socket = "server.sock"

            [logger]
            type = "Trell.ConsoleLogger"

            [storage]
            path = {{new StringValueSyntax(userDataRootDirectory)}}

            [worker.pool]
            size = 10

            [worker.limits]
            max_startup_duration = "1s"
            max_execution_duration = "15m"
            grace_period = "10s"

            [[Serilog.WriteTo]]
            Name = "Console"
            Args = {"OutputTemplate" = "[{Timestamp:HH:mm:ss} {ProcessId} {Level:u3}] {Message:lj}{NewLine}{Exception}"}

            [Serilog.MinimumLevel]
            Default = "Debug"
            Override = {"Microsoft" = "Warning"}
            """
        );

        AnsiConsole.WriteLine(configAlreadyExists ? $"Overwrote {configFilePath}" : $"Created {configFilePath}");

        var shouldSkipExample = settings.SkipExample || !AnsiConsole.Prompt(
            new TextPrompt<bool>("Would you like to create an example worker?")
            .AddChoice(true)
            .AddChoice(false)
            .DefaultValue(true)
            .WithConverter(choice => choice ? "y" : "n")
        );

        if (!shouldSkipExample) {
            const string INVALID_INPUT_MSG = "Name may contain only lowercase letters (a-z), numbers (0-9), and underscore (_)";

            var usernameIsValid = !string.IsNullOrWhiteSpace(settings.Username) && TrellPath.IsValidNameForFolder(settings.Username);
            var workerNameIsValid = !string.IsNullOrWhiteSpace(settings.WorkerName) && TrellPath.IsValidNameForFolder(settings.WorkerName);

            if (!usernameIsValid || !workerNameIsValid) {
                AnsiConsole.WriteLine("Use only lowercase letters (a-z), numbers (0-9), and underscore (_) for the following.");
            }
            var username = usernameIsValid
                ? settings.Username!
                : AnsiConsole.Prompt(
                    new TextPrompt<string>("Please provide a username")
                    .DefaultValue("new_user")
                    .ShowDefaultValue()
                    .Validate(TrellPath.IsValidNameForFolder, INVALID_INPUT_MSG)
                );
            var workerName = workerNameIsValid
                ? settings.WorkerName!
                : AnsiConsole.Prompt(
                    new TextPrompt<string>("Please provide a name for a new worker")
                    .DefaultValue("new_worker")
                    .ShowDefaultValue()
                    .Validate(TrellPath.IsValidNameForFolder, INVALID_INPUT_MSG)
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
            await File.WriteAllTextAsync(workerFilePath, """
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
