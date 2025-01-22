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
    [CommandArgument(0, "<worker-file-or-dir>"), Description("Either a worker's file path or a directory containing a worker.js to run")]
    public required string WorkerPath { get; set; }

    [CommandArgument(1, "<handler-fn>"), Description("Specifies which worker handler function to call: scheduled, fetch, or upload")]
    public Rpc.Function.ValueOneofCase HandlerFn { get; set; } = Rpc.Function.ValueOneofCase.None;

    [CommandArgument(2, "[data-file]"), Description("File path for data to upload or to fetch replay data")]
    public string? DataFile { get; set; }

    public override ValidationResult Validate() {
        if (string.IsNullOrWhiteSpace(this.WorkerPath)) {
            return ValidationResult.Error("A path for a worker file or directory needs to be specified");
        }
        var runDir = DirectoryHelper.GetFullPath(this.WorkerPath);
        if (File.Exists(runDir)) {
            // WorkerPath must either be the current directory or exist under our current directory if it's a file path
            if (Path.GetRelativePath(Directory.GetCurrentDirectory(), runDir) == runDir) {
                return ValidationResult.Error("'run' may only be called on files that exist in or under the current working directory");
            }
        } else if (!Directory.Exists(runDir)) {
            return ValidationResult.Error("'run' can only be called on a directory or file that exists");
        }
        if (this.HandlerFn == Rpc.Function.ValueOneofCase.None) {
            return ValidationResult.Error("A worker handler function must be passed as an argument");
        } else if (this.HandlerFn == Rpc.Function.ValueOneofCase.Upload) {
            var filePath = DirectoryHelper.GetFullPath(this.DataFile);
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
        // Makes all paths relative to worker root and sanitizes paths for TrellPath's use.
        var rootDir = Path.GetFullPath(config.Storage.Path);
        string Sanitize(string s) {
            if (Path.IsPathFullyQualified(s)) {
                s = Path.GetRelativePath(rootDir, s);
            }
            return s.Replace('\\', '/');
        }

        var dataPath = Sanitize(config.Storage.DataPath);

        var sharedDbDir = Path.GetFullPath(Path.Combine(dataPath, "shared"), rootDir);
        var sharedDbExists = Directory.Exists(sharedDbDir);
        List<string> sharedDbs = [];
        if (sharedDbExists) {
            foreach (var db in Directory.EnumerateFiles(sharedDbDir, "*.db", new EnumerationOptions {
                IgnoreInaccessible = true,
            })) {
                sharedDbs.Add("shared/" + Path.GetFileNameWithoutExtension(db));
            }
        }
        sharedDbDir = Sanitize(sharedDbDir);

        var codePath = Sanitize(rootDir);

        var workerPath = DirectoryHelper.GetFullPath(settings.WorkerPath);
        var fileName = Sanitize(File.Exists(workerPath) ? workerPath : Path.GetFullPath("worker.js", workerPath));

        var dataFilePath = DirectoryHelper.GetFullPath(settings.DataFile);

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
                    Function = GetFunction(settings.HandlerFn, dataFilePath),
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
                SharedDatabasesPath = sharedDbDir,
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
        var config = TrellConfig.LoadToml("Trell.toml");
        config.Storage.Path = ".";

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
