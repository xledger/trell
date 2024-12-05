using Serilog;
using System.Collections.Concurrent;
using Trell.Collections;
using Trell.Engine.Extensibility;
using Trell.Engine.Utility;

namespace Trell.IPC.Server;

sealed class WorkerPool : IDisposable {
    readonly TrellConfig config;
    readonly TrellExtensionContainer extensionContainer;

    readonly BoundedObjectPool<string, WorkerHandle> pool;
    readonly ConcurrentDictionary<int, TaskCompletionSource> pendingWorkersById = new();

    public WorkerPool(TrellConfig config, TrellExtensionContainer extensionContainer) {
        this.config = config;
        this.extensionContainer = extensionContainer;
        this.pool = new(StartNew, max: config.Worker.Pool.Size, pending: config.Worker.Pool.Pending);
        Log.Information("Started worker pool: size {Size}, pending {Pending}.",
            config.Worker.Pool.Size, config.Worker.Pool.Pending);
    }

    public IEnumerable<WorkerHandle> Handles => this.pool.Values;

    internal WorkerHandle GetWorkerHandle(string userId) {
        if (!this.pool.TryGet(userId, out var handle)) {
            throw new Exception("Too much work.");
        }

        return handle;
    }

    internal bool TrySetWorkerReady(int workerId) {
        if (this.pendingWorkersById.TryRemove(workerId, out var workerReady)) {
            workerReady.SetResult();
            return true;
        } else {
            return false;
        }
    }

    WorkerHandle StartNew() {
        var workerAddr = WorkerAddress.GetNext(this.config.Socket);
        var tcs = new TaskCompletionSource();
        Assert.IsTrue(this.pendingWorkersById.TryAdd(workerAddr.WorkerId, tcs));
        return WorkerHandle.Start(new WorkerOptions {
            Config = this.config,
            WorkerAddress = workerAddr,
            ConnectionReady = tcs.Task,
            Observer = this.extensionContainer.Observer,
            ExtensionContainer = this.extensionContainer,
        });
    }

    public void Dispose() {
        this.pool.Dispose();
    }
}
