using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.StaticFiles;
using Serilog;
using Serilog.Events;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Trell.Engine.ClearScriptWrappers;
using Trell.Extensions;
using Trell.IPC.Server;

namespace Trell.CliCommands;

public class RunCommandSettings : CommandSettings {
    [CommandArgument(0, "<handler-fn>"), Description("Worker handler to call: cron, request, or upload")]
    public string? HandlerFn { get; set; }

    [CommandArgument(1, "[data-file]"), Description("File path for data to upload")]
    public string? DataFile { get; set; }

    public override ValidationResult Validate() {
        if (string.IsNullOrEmpty(this.HandlerFn)) {
            return ValidationResult.Error("A worker handler must be passed as an argument");
        } else if (this.HandlerFn == "upload") {
            if (string.IsNullOrWhiteSpace(this.DataFile)
                || !File.Exists(Path.GetFullPath(this.DataFile))) {
                return ValidationResult.Error("Uploading requires a valid path for an existing file be passed as an argument");
            }
        }
        return ValidationResult.Success();
    }
}

public class RunCommand : AsyncCommand<RunCommandSettings> {
    int id = 0;
    string GetNextExecutionId() => $"id-{Interlocked.Increment(ref this.id)}";

    Rpc.ServerWorkOrder GetServerWorkOrder(RunCommandSettings settings, TrellConfig config) {
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
        var fileName = "worker.js";

        settings.DataFile = settings.DataFile.Maybe(Path.GetFullPath);

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
                    Function = GetFunction(settings.HandlerFn ?? "", settings.DataFile),
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

    static Rpc.Upload GenerateUpload(string? uploadDataPath) {
        if (string.IsNullOrWhiteSpace(uploadDataPath)) {
            return new();
        }
        uploadDataPath = Path.GetFullPath(uploadDataPath);
        var fileName = Path.GetFileName(uploadDataPath);
        var uploadDataBytes = File.ReadAllBytes(uploadDataPath);
        if (!new FileExtensionContentTypeProvider().TryGetContentType(fileName, out var fileType)) {
            // Fallback to ASP.Net's default MIME type for binary files
            fileType = "application/octet-stream";
        }
        return new() {
            Filename = fileName,
            Content = ByteString.CopyFrom(uploadDataBytes),
            Type = fileType,
        };
    }

    Rpc.Function GetFunction(string handler, string? uploadDataPath) {
        return handler switch {
            "cron" => new() {
                OnCronTrigger = new() {
                    Cron = "* * * * *",
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            },
            "request" =>
                //OnRequest = new() {
                //    Url = "http://localhost:9305/events/1/pay",
                //    Method = "POST",
                //    Headers = {
                //            { "Accept", "text/plain" },
                //            { "Accept-Language", "en/US" },
                //        },
                //    Body = ByteString.CopyFromUtf8("Update your payment records."),
                //},
                throw new NotImplementedException("Running request payloads from the CLI is not implemented yet."),
            "upload" => new() {
                OnUpload = GenerateUpload(uploadDataPath),
            },
            _ => throw new ArgumentOutOfRangeException(handler.ToString()),
        };
    }

    public override async Task<int> ExecuteAsync(CommandContext context, RunCommandSettings settings) {
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
