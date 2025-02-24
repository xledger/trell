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

namespace Trell.CliCommands;

public class InitCommandSettings : CommandSettings {
}

class InitCommand : AsyncCommand<InitCommandSettings> {
    public async override Task<int> ExecuteAsync(CommandContext context, InitCommandSettings settings) {
        var currentDir = Directory.GetCurrentDirectory();

        var configFilePath = Path.GetFullPath("Trell.toml", currentDir);
        var configAlreadyExists = File.Exists(configFilePath);

        var workerFilePath = Path.GetFullPath("worker.js", currentDir);
        var workerAlreadyExists = File.Exists(workerFilePath);

        var gitignoreFilePath = Path.GetFullPath(".gitignore", currentDir);
        var gitignoreAlreadyExists = File.Exists(gitignoreFilePath);

        var envFilePath = Path.GetFullPath(".env", currentDir);
        var envAlreadyExists = File.Exists(envFilePath);

        if (configAlreadyExists || workerAlreadyExists || gitignoreAlreadyExists || envAlreadyExists) {
            var shouldClobber = AnsiConsole.Prompt(
                new TextPrompt<bool>("Existing Trell files found. Continuing will overwrite the existing files. Are you sure you want to continue?")
                .AddChoice(true)
                .AddChoice(false)
                .DefaultValue(false)
                .WithConverter(choice => choice ? "y" : "n")
            );

            if (!shouldClobber) {
                AnsiConsole.WriteLine("Initialization terminated early, exiting...");
                return 1;
            }
        }

        await File.WriteAllTextAsync(configFilePath, $$"""
            socket = "server.sock"

            [logger]
            type = "Trell.ConsoleLogger"

            [storage]
            data_path = "data/"

            [[Serilog.WriteTo]]
            Name = "Console"
            Args = {"OutputTemplate" = "[{Timestamp:HH:mm:ss} {ProcessId} {Level:u3}] {Message:lj}{NewLine}{Exception}"}

            [Serilog.MinimumLevel]
            Default = "Debug"
            Override = {"Microsoft" = "Warning"}
            """
        );
        AnsiConsole.WriteLine(configAlreadyExists ? $"Overwrote {configFilePath}" : $"Created {configFilePath}");

        var dataDir = Path.GetFullPath("data", currentDir);
        if (!Directory.Exists(dataDir)) {
            Directory.CreateDirectory(dataDir);
            AnsiConsole.WriteLine($"Created {dataDir}");
        }

        await File.WriteAllTextAsync(workerFilePath, """
            async function onCronTrigger(context) {
                console.log("running cron trigger handler")
            }

            async function onRequest(context) {
                console.log("running request handler")
            }

            async function onUpload(context) {
                console.log("running upload handler")
            }

            export default {
                onCronTrigger,
                onRequest,
                onUpload,
            }
        
            """
        );
        AnsiConsole.WriteLine(workerAlreadyExists ? $"Overwrote {workerFilePath}" : $"Created {workerFilePath}");

        await File.WriteAllTextAsync(gitignoreFilePath, """
            *.sock
            *.env
            data/
        
            """
        );
        AnsiConsole.WriteLine(gitignoreAlreadyExists ? $"Overwrote {gitignoreFilePath}" : $"Created {gitignoreFilePath}");

        await File.WriteAllTextAsync(envFilePath, """
            # Place any environment variables/secrets in this file for worker development.
            # KEY="..."

            """
        );
        AnsiConsole.WriteLine(envAlreadyExists ? $"Overwrote {envFilePath}" : $"Created {envFilePath}");

        AnsiConsole.WriteLine($"""
            Trell worker created in {currentDir}.
            Run this worker's handlers with:
                trell run cron
                trell run upload example.csv
                trell run request payload.json
            """
        );
        return 0;
    }
}
