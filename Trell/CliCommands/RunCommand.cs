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

    [CommandOption("--url <url>"), Description("Sets the request's URL")]
    public string? Url { get; set; }

    [CommandOption("-H|--header <header-value>"), Description("Adds a header to the request")]
    public string[]? Headers { get; set; }

    public Dictionary<string, string>? ValidatedHeaders { get; private set; }

    public override ValidationResult Validate() {
        if (string.IsNullOrEmpty(this.HandlerFn)) {
            return ValidationResult.Error("A worker handler must be passed as an argument");
        } else if (this.HandlerFn == "upload") {
            if (string.IsNullOrWhiteSpace(this.DataFile)
                || !File.Exists(Path.GetFullPath(this.DataFile))) {
                return ValidationResult.Error("Uploading requires a valid path for an existing file be passed as an argument");
            }
        } else if (this.HandlerFn == "request") {
            if (string.IsNullOrWhiteSpace(this.DataFile)
                || !File.Exists(Path.GetFullPath(this.DataFile))) {
                return ValidationResult.Error("A valid path to an existing file is required for making requests");
            }
            if (this.Url is not null) {
                if (!this.Url.StartsWith("http://") && !this.Url.StartsWith("https://")) {
                    return ValidationResult.Error("HTTP requests must start with \"http://\" or \"https://\"");
                }
                var hostNameIdx = this.Url.IndexOf("://") + "://".Length;
                if (hostNameIdx == this.Url.Length) {
                    return ValidationResult.Error("Missing host name in URL");
                }
                var hostNameEndIdx = this.Url.IndexOfAny(['/', ':'], hostNameIdx);
                if (hostNameEndIdx < 0) {
                    hostNameEndIdx = this.Url.Length;
                }
                var hostName = this.Url[hostNameIdx..hostNameEndIdx];
                if (Uri.CheckHostName(hostName) == UriHostNameType.Unknown) {
                    return ValidationResult.Error($"Invalid host name given for URL: {hostName}");
                }
                if (!Uri.TryCreate(this.Url, UriKind.Absolute, out var validUri)) {
                    return ValidationResult.Error("An ill-formed URL was given as an argument");
                }
                this.Url = validUri.AbsoluteUri;
            }
            if (this.Headers is not null && this.Headers.Length > 0) {
                this.ValidatedHeaders = [];
                for (int i = 0; i < this.Headers.Length; i++) {
                    var str = this.Headers[i];
                    if (string.IsNullOrEmpty(str)) {
                        return ValidationResult.Error("Headers must be formatted like this: \"Header-Name: Header-Value\"");
                    }
                    var split = str.Split(':');
                    if (split.Length != 2) {
                        return ValidationResult.Error("Headers must be formatted like this: \"Header-Name: Header-Value\"");
                    }
                    this.ValidatedHeaders[split[0].Trim()] = split[1].Trim();
                }
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
                    Function = GetFunction(settings),
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

    static Rpc.Request GenerateRequest(RunCommandSettings settings) {
        if (string.IsNullOrWhiteSpace(settings.DataFile)) {
            return new();
        }
        var requestDataPath = Path.GetFullPath(settings.DataFile);
        var fileName = Path.GetFileName(requestDataPath);
        var requestDataBytes = File.ReadAllBytes(requestDataPath);
        if (!new FileExtensionContentTypeProvider().TryGetContentType(fileName, out var fileType)) {
            // Fallback to ASP.Net's default MIME type for binary files
            fileType = "application/octet-stream";
        }

        Dictionary<string, string> headers = settings.ValidatedHeaders ?? [];
        if (!headers.ContainsKey("Content-Encoding")) {
            headers["Content-Encoding"] = fileType;
        }

        return new() {
            Url = settings.Url ?? "http://www.example.com/fetch",
            Method = "POST",
            Headers = {
                headers,
            },
            Body = ByteString.CopyFrom(requestDataBytes),
        };
    }

    static Rpc.Upload GenerateUpload(RunCommandSettings settings) {
        if (string.IsNullOrWhiteSpace(settings.DataFile)) {
            return new();
        }
        var uploadDataPath = Path.GetFullPath(settings.DataFile);
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

    Rpc.Function GetFunction(RunCommandSettings settings) {
        var handler = settings.HandlerFn ?? "";
        return handler switch {
            "cron" => new() {
                OnCronTrigger = new() {
                    Cron = "* * * * *",
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            },
            "request" => new() {
                OnRequest = GenerateRequest(settings),
            },  
            "upload" => new() {
                OnUpload = GenerateUpload(settings),
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
