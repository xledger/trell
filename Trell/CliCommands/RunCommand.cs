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
    /// <summary>
    /// Path to Trell TOML config
    /// </summary>
    [CommandOption("--config")]
    public string? Config { get; set; }

    [CommandOption("-u|--user-id <user-id>"), DefaultValue("new_user"), Description("User id to send in work order; also used for worker id resolution")]
    public string UserId { get; set; } = "new_user";

    [CommandOption("--shared-db-dir <shared-db-dir>"), Description("Folder containing databases shared between multiple processes")]
    public string? SharedDbDir { get; set; }

    [CommandOption("--worker-db-dir <worker-db-dir>"), Description("Folder containing worker databases")]
    public string? WorkerDbDir { get; set; }

    [CommandArgument(0, "<run-dir>"), Description("The directory from which to run worker file")]
    public required string RunDir { get; set; }

    [CommandArgument(1, "<handler-fn>"), Description("Specifies which worker handler function to call: scheduled, fetch, or upload")]
    public Rpc.Function.ValueOneofCase HandlerFn { get; set; } = Rpc.Function.ValueOneofCase.None;

    [CommandArgument(2, "[upload-data-path]"), Description("Path to data to be uploaded")]
    public string? UploadDataPath { get; set; }

    public override ValidationResult Validate() {
        var fullPath = Path.GetFullPath(this.RunDir);
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath)) {
            return ValidationResult.Error("'run' can only be called on a directory or file that exists");
        }
        if (this.HandlerFn == Rpc.Function.ValueOneofCase.None) {
            return ValidationResult.Error("A worker handler function must be passed as an argument");
        } else if (this.HandlerFn == Rpc.Function.ValueOneofCase.Upload) {
            if (string.IsNullOrWhiteSpace(this.UploadDataPath)) {
                return ValidationResult.Error("Missing required path for data to upload");
            }
            var fileLoc = Path.IsPathFullyQualified(this.UploadDataPath)
                ? this.UploadDataPath
                : Path.GetFullPath(this.UploadDataPath, Path.GetDirectoryName(fullPath)!);
            if (!File.Exists(fileLoc)) {
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
        List<string> sharedDbs = string.IsNullOrWhiteSpace(settings.SharedDbDir) ? [] : [settings.SharedDbDir];
        var fullPath = Path.GetFullPath(settings.RunDir);
        string codePath;
        string? fileName;
        if (File.Exists(fullPath)) {
            codePath = Path.GetDirectoryName(fullPath)!;
            fileName = Path.GetFileName(fullPath);
        } else {
            codePath = fullPath;
            fileName = null;
        }

        var uploadDataPath = settings.UploadDataPath;
        if (!string.IsNullOrEmpty(uploadDataPath) && !Path.IsPathFullyQualified(uploadDataPath)) {
            uploadDataPath = Path.GetFullPath(uploadDataPath, codePath);
        }

        codePath = Path.GetRelativePath(config.Storage.Path, codePath).Replace('\\', '/');

        var dataPath = string.IsNullOrWhiteSpace(settings.WorkerDbDir) || settings.WorkerDbDir == "%"
            ? codePath
            : settings.WorkerDbDir;

        return new Rpc.ServerWorkOrder {
            WorkOrder = new() {
                User = new() {
                    UserId = settings.UserId,
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
                    Function = GetFunction(settings.HandlerFn, uploadDataPath),
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

    Rpc.Function GetFunction(Rpc.Function.ValueOneofCase @case, string? uploadDataPath) {
        return @case switch {
            Rpc.Function.ValueOneofCase.Scheduled => new() {
                Scheduled = new() {
                    Cron = "* * * * *",
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            },
            Rpc.Function.ValueOneofCase.Webhook => new() {
                // @TODO: Allow passing this from a HAR file. (Right click a request in Chrome, save as .har).
                Webhook = new() {
                    Url = "http://localhost:9305/webhooks/1/pay",
                    Method = "POST",
                    Headers = {
                        { "Accept", "text/plain" },
                        { "Accept-Language", "en/US" },
                    },
                    Body = ByteString.CopyFromUtf8("Update your payment records."),
                },
            },
            Rpc.Function.ValueOneofCase.Upload => new() {
                Upload = GenerateUpload(uploadDataPath),
            },
            Rpc.Function.ValueOneofCase.Dynamic => new() {
                Dynamic = new() {
                    Name = "process",
                },
            },
            _ => throw new ArgumentOutOfRangeException(@case.ToString()),
        };
    }

    public override async Task<int> ExecuteAsync(CommandContext context, RunCommandSettings settings) {
        settings.Validate();
        var config = TrellConfig.LoadToml(settings.Config ?? "Trell.toml");

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
