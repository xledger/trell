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

namespace Trell.CliCommands;

public class InitCommandSettings : CommandSettings {
    [CommandOption("-d|--user-data-dir <directory>"), Description("Directory to use for trell user/worker data.")]
    public string? UserDataDirectory { get; set; }

    [CommandOption("-u|--username <username>"), Description("New username to initialize workers for.")]
    public string? Username { get; set; }

    [CommandOption("-w|--worker-name <worker-name>"), Description("Name of new worker to initialize.")]
    public string? WorkerName { get; set; }

    [CommandOption("-e|--create-example"), Description("Create a new example worker to demonstrate worker capabilities.")]
    public bool CreateExample { get; set; }
}

class InitCommand : AsyncCommand<InitCommandSettings> {
    public async override Task<int> ExecuteAsync(CommandContext context, InitCommandSettings settings) {
        // TODO: right now we are just creating the config TOML file in the current working directory.
        // The user is expected to remember where the TOML file was created and pass that path in as a parameter.
        // I strongly recommend at some point we use a defined location to build this file to instead, such as:
        // C:\Program Files\Xledger\Trell\Config
        // Creating the file in the same directory as this assembly would also work as a fixed location.
        var configAlreadyExists = File.Exists(CONFIG_FILE);
        var config = configAlreadyExists
            ? TrellConfig.LoadToml(CONFIG_FILE)
            : TrellConfig.CreateNew();

        string? userDataRootDirectory = null;
        if (settings.UserDataDirectory is not null) {
            userDataRootDirectory = Path.GetFullPath(settings.UserDataDirectory);
        } else if (configAlreadyExists && !string.IsNullOrWhiteSpace(config.Storage.Path)) {
            var reuseConfigPath = AnsiConsole.Prompt(
                new TextPrompt<bool>($"Config found. Continue using {config.Storage.Path} for storing user data?")
                .AddChoice(true)
                .AddChoice(false)
                .DefaultValue(true)
                .WithConverter(choice => choice ? "y" : "n")
            );

            if (reuseConfigPath) {
                userDataRootDirectory = config.Storage.Path;
            }
        }

        // TODO: validate user input
        userDataRootDirectory ??= AnsiConsole.Prompt(
            new TextPrompt<string>("Please provide a path for where to store user data")
            .DefaultValue(DEFAULT_USER_DATA_ROOT_DIR)
            .ShowDefaultValue()
        );

        if (!Directory.Exists(userDataRootDirectory)) {
            Directory.CreateDirectory(userDataRootDirectory);
            Log.Information("Created {dir}", userDataRootDirectory);
        }

        if (!configAlreadyExists || userDataRootDirectory != config.Storage.Path) {
            config.Storage.Path = userDataRootDirectory;
            using var trellCfgFs = File.Open(CONFIG_FILE, mode: FileMode.OpenOrCreate, access: FileAccess.ReadWrite);
            using var sw = new StreamWriter(trellCfgFs);
            if (!config.TryConvertToToml(out var configAsText)) {
                throw new TrellException("Unable to convert config to TOML");
            }
            await sw.WriteLineAsync(configAsText);

            Log.Information(configAlreadyExists ? "Updated existing {file}" : "Wrote {file}", CONFIG_FILE);
        }

        // TODO: validate user input for usernames and worker names, and/or expand TrellPath's valid characters.
        // We use TrellPath.TryParseRelative to navigate to some of these folders, so if the names given contain
        // any characters like 'ø' it can fail TrellPath's validation for paths
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

        MakeDirectoriesForNewWorker(userDataRootDirectory, username, workerName);
        Log.Information("Created directories for ( user: {u}, worker: {w} )", username, workerName);

        // Users need, at bare minimum, a template worker.js to start from
        var workerPath = Path.GetFullPath(WORKER_FILE, GetWorkerSrcPath(userDataRootDirectory, username, workerName));
        if (File.Exists(workerPath)) {
            Log.Information("Worker file already exists at worker directory, skipping.");
        } else {
            using var workerJsFs = File.Open(workerPath, mode: FileMode.CreateNew, access: FileAccess.ReadWrite);
            using var sw2 = new StreamWriter(workerJsFs);
            await sw2.WriteLineAsync("""
                async function scheduled(event, env, ctx) {
                    // Add work here you would like to schedule
                    return "scheduled completed"
                }

                function fetch(request, env, ctx) {
                    // Add logic here to fetch data
                    return "fetch completed"
                }

                function upload(payload, env, ctx) {
                    // Add logic here to upload data
                    return "upload completed"
                }

                export default {
                    scheduled,
                    fetch,
                    upload,
                }
        
                """
            );
        }

        const string EXAMPLE_WORKER_NAME = "example-worker";
        var exampleSrcDir = GetWorkerSrcPath(userDataRootDirectory, username, EXAMPLE_WORKER_NAME);
        var workerJsPath = Path.GetFullPath(WORKER_FILE, exampleSrcDir);
        if (File.Exists(workerJsPath)) {
            Log.Information("Example worker already exists, skipping.");

            if (settings.CreateExample) {
                AnsiConsole.WriteLine($"""
                    Example worker already exists at {exampleSrcDir}
                    You can run the example worker commands with:
                        trell run --user-id {username} --handler scheduled worker {EXAMPLE_WORKER_NAME}
                        trell run --user-id {username} --handler fetch worker {EXAMPLE_WORKER_NAME}
                        trell run --user-id {username} --handler upload worker {EXAMPLE_WORKER_NAME}
                    """
                );
            }
        } else {
            var shouldCreateExample = settings.CreateExample || AnsiConsole.Prompt(
                new TextPrompt<bool>("Create Example Worker?")
                .AddChoice(true)
                .AddChoice(false)
                .DefaultValue(true)
                .WithConverter(choice => choice ? "y" : "n")
            );

            if (shouldCreateExample) {
                MakeDirectoriesForNewWorker(userDataRootDirectory, username, EXAMPLE_WORKER_NAME);

                if (File.Exists(workerJsPath)) {
                    Log.Information("Example worker already exists, skipping.");
                } else {
                    using var workerJsFs = File.Open(workerJsPath, mode: FileMode.CreateNew, access: FileAccess.ReadWrite);
                    using var sw2 = new StreamWriter(workerJsFs);

                    // TODO: we can use the sample worker to demonstrate more complex tasks than this for new users.
                    // At some point we should revisit what this file contains.
                    await sw2.WriteLineAsync("""
                        async function scheduled(event, env, ctx) {
                            console.log("Example 'scheduled' called")
                            return "scheduled completed"
                        }

                        function fetch(request, env, ctx) {
                            console.log("Example 'fetch' called")
                            return "fetch completed"
                        }

                        function upload(payload, env, ctx) {
                            console.log("Example 'upload' called")
                            return "upload completed"
                        }

                        export default {
                            scheduled,
                            fetch,
                            upload,
                        }
        
                        """
                    );
                }

                AnsiConsole.WriteLine($"""
                    Example worker created at {exampleSrcDir}
                    You can run the example worker commands with:
                        trell run --user-id {username} --handler scheduled worker {EXAMPLE_WORKER_NAME}
                        trell run --user-id {username} --handler fetch worker {EXAMPLE_WORKER_NAME}
                        trell run --user-id {username} --handler upload worker {EXAMPLE_WORKER_NAME}
                    """
                );
            }
        }

        AnsiConsole.WriteLine("Trell initialization complete");

        return 0;
    }
}
