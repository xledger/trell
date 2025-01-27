using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Engine.Utility;
using Trell.Extensions;
using Trell.Rpc;
using Trell.Usage;

namespace Trell.IPC.Server;

enum HandleState {
    Uninitialized,
    Starting,
    Available,
    Recycling,
}

sealed class WorkerHandle {
    HandleState state;
    readonly WorkerOptions options;
    readonly Process process;
    readonly Lazy<IWorkerClient> client;
    internal Lazy<IMetricsProducer> tracker;

    public WorkerOptions Options => this.options;
    public Process Process => this.process;

    ITrellObserver Observer => this.options.Observer;

    readonly ConcurrentDictionary<string, Execution> executionsById = new ConcurrentDictionary<string, Execution>();
    internal IReadOnlyList<Execution> Executions => this.executionsById.Values.ToList();

    IWorkerClient Client => this.client.Value;

    WorkerHandle(WorkerOptions options, Process process) {
        this.options = options;
        this.process = process;
        this.client = new Lazy<IWorkerClient>(NewClient);
        this.tracker = new Lazy<IMetricsProducer>(NewTracker);
    }

    WorkerHandle(WorkerOptions options, Process process, IWorkerClient singleton) {
        this.options = options;
        this.process = process;
        this.client = new Lazy<IWorkerClient>(singleton);
        this.tracker = new Lazy<IMetricsProducer>(NewTracker);
    }

    public bool IsAvailable => this.state == HandleState.Available;
    public bool IsRecycling => this.state == HandleState.Recycling;

    WorkerClient NewClient() => new WorkerClient(this, NewRpcClient());

    IMetricsProducer NewTracker() {
        var tracker = new EventCountersWorkerMetrics(this, this.options.Observer);
        _ = Task.Run(() => {
            try {
                tracker.PollProcessMetrics();
            } catch (Exception ex) {
                Log.Fatal("PollProcessMetrics failed {ex}", ex);
            }
        });

        return tracker;
    }

    TrellWorker.TrellWorkerClient NewRpcClient() {
        var workerCh = Utility.CreateUnixDomainSocketChannel(this.options.WorkerAddress.SocketPath);
        var workerRpcClient = new TrellWorker.TrellWorkerClient(workerCh);
        return workerRpcClient;
    }

    public async Task<IWorkerClient> GetClientAsync(CancellationToken tok = default) {
        switch (this.state) {
            case HandleState.Uninitialized:
            case HandleState.Starting:
                await WaitForAvailableAsync(tok);
                return this.Client;

            case HandleState.Available:
                return this.Client;

            default:
            case HandleState.Recycling:
                throw new Exception();
        }
    }

    async Task WaitForAvailableAsync(CancellationToken tok) {
        // Wait for connection to be ready.
        await this.options.ConnectionReady.WaitAsync(TimeSpan.FromSeconds(5), tok);
        this.state = HandleState.Available;
    }

    public static WorkerHandle Start(WorkerOptions opts) {
        if (Process.GetCurrentProcess()?.MainModule?.FileName is not string processPath) {
            throw new InvalidOperationException("Could not find process path");
        }
        Log.Debug("ProcessPath {p} {args}", processPath, new[] {
                "worker",
                "--config",
                opts.Config.ConfigPath,
                opts.WorkerAddress.WorkerId.ToString(),
                opts.WorkerAddress.SocketPath
            });
        var procStart = new ProcessStartInfo {
            FileName = processPath,
            ArgumentList = {
                    "worker",
                    "--config",
                    opts.Config.ConfigPath,
                    opts.WorkerAddress.WorkerId.ToString(),
                    opts.WorkerAddress.SocketPath
                },
            RedirectStandardInput = true,
        };
        var process = Process.Start(procStart);
        Assert.NotNull(process);
        var handle = new WorkerHandle(opts, process);
        Log.Information("Worker {Id} (process {Pid}) started.",
            opts.WorkerAddress.WorkerId,
            process.Id);
        return handle;
    }

    public static WorkerHandle InProcess(WorkerOptions opts) {
        // Instantiate an in-process Trell worker and V8 runtime.
        var rt = new RuntimeWrapper(opts.ExtensionContainer, opts.Config.ToRuntimeConfig());
        var worker = new Worker.TrellWorkerCore(opts.Config, opts.ExtensionContainer, rt);

        var process = Process.GetCurrentProcess();
        var handle = new WorkerHandle(opts, process, worker);
        Log.Information("Worker {Id} running in current process ({Pid}).",
            opts.WorkerAddress.WorkerId,
            process.Id);
        return handle;
    }

    internal IDisposable Track(WorkOrder request) {
        this.executionsById.TryAdd(request.ExecutionId, new Execution(request.User.UserId, request.User.Data.ToDictionary(), request.ExecutionId));

        this.Observer.OnJobStarted(request.ExecutionId);
        this.tracker.Value.EmitMetrics(MetricsTrigger.JobStart);

        // For tracking database use by user id and worker id.
        var userId = request.User.UserId;
        var workerId = request.Workload.WorkerId;

        return Disposable.FromAction(() => {
            // The execution should be removed after the next metrics sent
            // since the resources this execId used won't have been
            // logged with it until that point.
            IDisposable? d = null;
            d = this.tracker.Value.OnEmitMetrics(trigger => {
                if (trigger != MetricsTrigger.Interval) {
                    return;
                }

                d?.Dispose();
                this.executionsById.TryRemove(request.ExecutionId, out var _);
            });

            var dbs = request.SharedDatabases.ToList();
            if (workerId is not null) {
                var workerDataDir = Path.Join(this.options.Config.Storage.Path, "users", userId, "workers", workerId, "data");
                if (Directory.Exists(workerDataDir)) {
                    foreach (var db in Directory.EnumerateFiles(workerDataDir, "*.db", new EnumerationOptions {
                        IgnoreInaccessible = true,
                    })) {
                        dbs.Add(Path.GetFileNameWithoutExtension(db));
                    }
                }
            }

            this.tracker.Value.TrackDatabases(userId, workerId, dbs);
            this.Observer.OnJobStopped(request.ExecutionId);
            this.tracker.Value.EmitMetrics(MetricsTrigger.JobStop);
        });
    }
}
