using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using Spectre.Console;
using Spectre.Console.Cli;
using Trell.Engine.ClearScriptWrappers;
using Trell.Extensions;

namespace Trell.CliCommands;

public class RunFileCommandSettings : CommandSettings {
    [CommandArgument(0, "<file-name>"), Description("Name of the file to run")]
    public string? FileName { get; set; }

    [CommandArgument(1, "<function-name>"), Description("Name of the function to run")]
    public string? FuncName { get; set; }

    [CommandArgument(2, "[optional-args]"), Description("""
        Optional arguments to pass to the function.
        Arguments will be wrapped into a single object: { argv: [[ "arg1", "arg2", ... ]] }
        """)]
    public string[]? Args { get; set; }

    public override ValidationResult Validate() {
        var filePath = Path.GetFullPath(this.FileName!);
        if (!File.Exists(filePath)) {
            return ValidationResult.Error($"No file found at: {filePath}");
        } else if (Path.GetExtension(filePath) != ".js") {
            return ValidationResult.Error("File provided must be a JavaScript file (.js)");
        }
        return ValidationResult.Success();
    }
}

public class RunFileCommand : AsyncCommand<RunFileCommandSettings> {
    int id = 0;
    string GetNextExecutionId() => $"dyn-id-{Interlocked.Increment(ref this.id)}";

    Rpc.ServerWorkOrder GetServerWorkOrder(RunFileCommandSettings settings, TrellConfig config) {
        // Makes all paths relative to worker root and sanitizes paths for TrellPath's use.
        var rootDir = Path.GetFullPath(config.Storage.Path);

        var dataPath = config.Storage.DataPath;

        var relativeSharedDbDir = Path.Combine(dataPath, "shared");
        List<string> sharedDbs = [];

        { // @FIXME - Find a better place to put this
            var sharedDbDir = Path.GetFullPath(relativeSharedDbDir, rootDir);
            var sharedDbExists = Directory.Exists(sharedDbDir);
            if (sharedDbExists) {
                foreach (var db in Directory.EnumerateFiles(sharedDbDir, "*.db", new EnumerationOptions {
                    IgnoreInaccessible = true,
                })) {
                    sharedDbs.Add("shared/" + Path.GetFileNameWithoutExtension(db));
                }
            }
        }

        var codePath = config.Storage.Path;
        var fileName = Path.GetRelativePath(rootDir, Path.GetFullPath(settings.FileName!)).Replace('\\', '/');

        return new Rpc.ServerWorkOrder {
            WorkOrder = new() {
                User = new() {
                    Data = new() {
                        Associations = {
                            new Rpc.Association() {
                                Key = "name",
                                String = Environment.UserName
                            },
                        },
                    },
                },
                Workload = new() {
                    Function = new() {
                        Dynamic = new() {
                            Name = settings.FuncName,
                            Arguments = {
                                settings.Args ?? [],
                            }
                        }
                    },
                    Data = new() {
                        Text = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() }),
                    },
                    CodePath = codePath,
                    DataPath = dataPath,
                    WorkerFilename = fileName,
                },
                SharedDatabases = {
                    sharedDbs,
                },
                SharedDatabasesPath = relativeSharedDbDir,
            },
        };
    }

    public override async Task<int> ExecuteAsync(CommandContext context, RunFileCommandSettings settings) {
        settings.Validate();
        App.BootstrapLogger(null);
        var config = TrellConfig.LoadToml("Trell.toml");

        var extensionContainer = TrellSetup.ExtensionContainer(config);
        var runtimeWrapper = new RuntimeWrapper(extensionContainer, config.ToRuntimeConfig());
        var worker = new IPC.Worker.TrellWorkerCore(config, extensionContainer, runtimeWrapper);

        Log.Information("Run: Executing worker in-process.");

        async Task<Rpc.WorkResult> Exec() {
            var workOrder = GetServerWorkOrder(settings, config);
            workOrder.WorkOrder.ExecutionId = GetNextExecutionId();
            var result = await worker.ExecuteAsync(workOrder.WorkOrder);
            Log.Information("Run: Execution Id: {Id} Result: {Result}",
                workOrder.WorkOrder.ExecutionId, result);
            return result;
        }

        await Exec();

        return 0;
    }
}
