using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Serilog;
using Serilog.Events;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;
using Trell.IPC.Server;

namespace Trell.CliCommands;

public class RunCommandSettings : CommandSettings {
    /// <summary>
    /// Path to Trell TOML config
    /// </summary>
    [CommandOption("--config")]
    public required string Config { get; set; }

    [CommandOption("--log-level")]
    public LogEventLevel? LogLevel { get; set; } = null;

    [CommandOption("-u|--user-id <user-id>"), DefaultValue("dummy-user"), Description("User id to send in work order; also used for worker id resolution")]
    public string UserId { get; set; } = "dummy-user";

    [CommandOption("--handler"), DefaultValue(Rpc.Function.ValueOneofCase.None), Description("Specifies which worker handler function to call: scheduled, fetch, or upload")]
    public Rpc.Function.ValueOneofCase HandlerFn { get; set; } = Rpc.Function.ValueOneofCase.None;

    [CommandOption("-n <count>"), DefaultValue(1), Description("Number of times to execute the code on the server")]
    public int ExecutionCount { get; set; } = 1;

    public override Spectre.Console.ValidationResult Validate() {
        if (this.HandlerFn == Rpc.Function.ValueOneofCase.None) {
            return Spectre.Console.ValidationResult.Error("--handler is required");
        }
        return base.Validate();
    }
}

public class RunWorkerIdCommandSettings : RunCommandSettings {
    [CommandArgument(0, "<worker-id>"), Description("Worker id to tell the server to execute")]
    public string? WorkerId { get; set; }
}

public abstract class RunCommand<T> : AsyncCommand<T> where T : RunCommandSettings {
    int id = 0;
    string GetNextExecutionId() => $"id-{Interlocked.Increment(ref this.id)}";

    protected abstract Rpc.ServerWorkOrder GetServerWorkOrder(T settings, TrellConfig config);

    protected Rpc.Function GetFunction(Rpc.Function.ValueOneofCase @case) {
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
                Upload = new() { },
            },
            Rpc.Function.ValueOneofCase.Dynamic => new() {
                Dynamic = new() {
                    Name = "process",
                },
            },
            _ => throw new ArgumentOutOfRangeException(@case.ToString()),
        };
    }

    public override async Task<int> ExecuteAsync(CommandContext context, T settings) {
        settings.Validate();
        App.BootstrapLogger(settings.LogLevel);
        var config = TrellConfig.LoadToml(settings.Config);
        var args = context.Remaining.Raw.ToArray();
        using var app = App.InitServer(config, args);
        await app.StartAsync();

        try {
            var serverSocket = config.Socket;

            Log.Information("Run: Connecting to {s}", serverSocket);

            var serverCh = Utility.CreateUnixDomainSocketChannel(serverSocket);
            var client = new Rpc.TrellServer.TrellServerClient(serverCh);

            async Task<Rpc.WorkResult> Exec() {
                var workOrder = GetServerWorkOrder(settings, config);
                workOrder.WorkOrder.ExecutionId = GetNextExecutionId();
                var result = await client.ExecuteAsync(workOrder);
                Log.Information("Run: Execution Id: {Id} Result: {Result}", workOrder.WorkOrder.ExecutionId, result);
                return result;
            }

            var tasks = new Task[settings.ExecutionCount];
            for (var i = 0; i < tasks.Length; ++i) {
                tasks[i] = Exec();
            }

            await Task.WhenAll(tasks);
        } finally {
            await app.StopAsync();
        }

        return 0;
    }
}

public class RunWorkerIdCommand : RunCommand<RunWorkerIdCommandSettings> {
    protected override Rpc.ServerWorkOrder GetServerWorkOrder(
        RunWorkerIdCommandSettings settings,
        TrellConfig config
    ) {
        var userId = settings.UserId;
        var workerId = settings.WorkerId;
        var userDataDir = Path.Join(config.Storage.Path, "users", userId, "data");
        var workerDataDir = Path.Join(config.Storage.Path, "users", userId, "workers", workerId, "data");
        List<string> sharedDbs = [];
        if (Directory.Exists(userDataDir)) {
            foreach (var db in Directory.EnumerateFiles(userDataDir, "*.db", new EnumerationOptions {
                IgnoreInaccessible = true,
            })) {
                sharedDbs.Add("shared/" + Path.GetFileNameWithoutExtension(db));
            }
        }

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
                    Function = GetFunction(settings.HandlerFn),
                    WorkerId = settings.WorkerId,
                    Data = new() {
                        Text = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() }),
                    },
                },
                SharedDatabases = {
                    sharedDbs
                },
            },
        };
    }
}
