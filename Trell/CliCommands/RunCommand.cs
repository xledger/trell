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

    [CommandOption("-u|--user <user-id>"), DefaultValue("new_user"), Description("User to send in work order")]
    public string UserId { get; set; } = "new_user";

    [CommandOption("--shared-db-dir <shared-db-dir>"), Description("Folder containing databases shared between multiple workers")]
    public string? SharedDbDir { get; set; }

    [CommandOption("--worker-db-dir <worker-db-dir>"), Description("Folder containing worker databases")]
    public string? WorkerDbDir { get; set; }

    [CommandArgument(0, "<worker-file-or-dir>"), Description("Either a worker's file path or a directory containing a worker.js to run")]
    public required string WorkerPath { get; set; }

    [CommandArgument(1, "<handler-fn>"), Description("Specifies which worker handler function to call: scheduled, fetch, or upload")]
    public Rpc.Function.ValueOneofCase HandlerFn { get; set; } = Rpc.Function.ValueOneofCase.None;

    [CommandArgument(2, "[data-path]"), Description("Path to upload data or path to fetch replay data")]
    public string? UploadDataPath { get; set; }

    public override ValidationResult Validate() {
        if (string.IsNullOrWhiteSpace(this.WorkerPath)) {
            return ValidationResult.Error("A path for a worker file or directory needs to be specified");
        }
        var runDir = DirectoryHelper.GetFullPath(this.WorkerPath);
        if (File.Exists(runDir)) {
            // WorkerPath must exist under our current directory if it's a file path
            if (Path.GetRelativePath(Directory.GetCurrentDirectory(), runDir) == runDir) {
                return ValidationResult.Error("'run' may only be called on files that exist under the current working directory");
            }
        } else if (!Directory.Exists(runDir)) {
            return ValidationResult.Error("'run' can only be called on a directory or file that exists");
        }
        if (this.HandlerFn == Rpc.Function.ValueOneofCase.None) {
            return ValidationResult.Error("A worker handler function must be passed as an argument");
        } else if (this.HandlerFn == Rpc.Function.ValueOneofCase.Upload) {
            var filePath = DirectoryHelper.GetFullPath(this.UploadDataPath);
            if (!File.Exists(filePath)) {
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
        // Strips the volume directory if the path isn't relative and switches out '\' with '/'
        // to sanitize paths for TrellPath's use.
        static string Sanitize(string s) {
            if (Path.IsPathFullyQualified(s)) {
                var volumeDir = Path.GetFullPath("/");
                s = Path.GetRelativePath(volumeDir, s);
            }
            return s.Replace('\\', '/');
        }

        var userDataDir = DirectoryHelper.GetFullPath(settings.SharedDbDir);
        List<string> sharedDbs = [];
        if (Directory.Exists(userDataDir)) {
            foreach (var db in Directory.EnumerateFiles(userDataDir, "*.db", new EnumerationOptions {
                IgnoreInaccessible = true,
            })) {
                sharedDbs.Add("shared/" + Path.GetFileNameWithoutExtension(db));
            }
        }

        var workerPath = DirectoryHelper.GetFullPath(settings.WorkerPath);
        string codePath;
        string fileName;
        if (File.Exists(workerPath)) {
            var currentDir = Directory.GetCurrentDirectory();
            codePath = Sanitize(currentDir);
            fileName = Sanitize(Path.GetRelativePath(currentDir, workerPath));
        } else {
            codePath = Sanitize(workerPath);
            fileName = "worker.js";
        }

        var dataPath = Sanitize(DirectoryHelper.GetFullPath(settings.WorkerDbDir));

        var uploadDataPath = DirectoryHelper.GetFullPath(settings.UploadDataPath);

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
        App.BootstrapLogger(null);
        var config = TrellConfig.LoadToml(settings.Config ?? "Trell.toml");
        config.Storage.Path = "/";

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
