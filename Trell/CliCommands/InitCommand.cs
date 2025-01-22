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
}

class InitCommand : AsyncCommand<InitCommandSettings> {
    public async override Task<int> ExecuteAsync(CommandContext context, InitCommandSettings settings) {
        var currentDir = Directory.GetCurrentDirectory();
        var configFilePath = Path.GetFullPath("Trell.toml", currentDir);
        var configAlreadyExists = File.Exists(configFilePath);

        if (configAlreadyExists) {
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

        await File.WriteAllTextAsync(configFilePath, $$"""
            socket = "server.sock"

            [logger]
            type = "Trell.ConsoleLogger"

            [storage]
            path = {{new StringValueSyntax(currentDir)}}
            data_path = "data/"

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

        var dataDir = Path.GetFullPath("data", currentDir);
        if (!Directory.Exists(dataDir)) {
            Directory.CreateDirectory(dataDir);
            AnsiConsole.WriteLine($"Created {dataDir}");
        }

        var workerFilePath = Path.GetFullPath("worker.js", currentDir);
        var workerAlreadyExists = File.Exists(workerFilePath);

        if (workerAlreadyExists) {
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

        var gitignoreFilePath = Path.GetFullPath(".gitignore", currentDir);
        var gitignoreAlreadyExists = File.Exists(gitignoreFilePath);

        if (gitignoreAlreadyExists) {
            var shouldClobberGitignore = AnsiConsole.Prompt(
                new TextPrompt<bool>("Existing .gitgnore found. Continuing will overwrite the existing file. Are you sure you want to continue?")
                .AddChoice(true)
                .AddChoice(false)
                .DefaultValue(false)
                .WithConverter(choice => choice ? "y" : "n")
            );

            if (!shouldClobberGitignore) {
                AnsiConsole.WriteLine("Initialization terminated early, exiting...");
                return 1;
            }
        }

        await File.WriteAllTextAsync(gitignoreFilePath, """
            *.sock
            data/
        
            """
        );
        AnsiConsole.WriteLine(gitignoreAlreadyExists ? $"Overwrote {gitignoreFilePath}" : $"Created {gitignoreFilePath}");

        AnsiConsole.WriteLine("Trell initialization complete");
        AnsiConsole.WriteLine($"""
            You can run worker commands with:
                trell run . scheduled
                trell run . upload example.csv
                trell run . fetch request.json
            """
        );
        return 0;
    }
}
