using System.Collections.Concurrent;
using Serilog;
using Trell.Engine.Collections;
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
        Func<WorkerHandle> startNew = config.Worker.Pool.SingleProcess ? StartNewInProcess : StartNew;
        pool = new BoundedObjectPool<string, WorkerHandle>(
            startNew,
            max: config.Worker.Pool.Size,
            pending: config.Worker.Pool.Pending
        );
        if (config.Worker.Pool.SingleProcess) {
            Log.Information(
                "Started worker pool in the current process: size {Size}, pending {Pending}.",
                config.Worker.Pool.Size,
                config.Worker.Pool.Pending
            );
        } else {
            Log.Information(
                "Started worker pool: size {Size}, pending {Pending}.",
                config.Worker.Pool.Size,
                config.Worker.Pool.Pending
            );
        }
    }

    public IEnumerable<WorkerHandle> Handles => pool.Values;

    internal WorkerHandle GetWorkerHandle(string userId) {
        if (!pool.TryGet(userId, out var handle)) {
            throw new Exception("Too much work.");
        }

        return handle;
    }

    internal bool TrySetWorkerReady(int workerId) {
        if (pendingWorkersById.TryRemove(workerId, out var workerReady)) {
            workerReady.SetResult();
            return true;
        } else {
            return false;
        }
    }

    WorkerHandle StartNew() {
        var workerAddr = WorkerAddress.GetNext(config.Socket);
        var tcs = new TaskCompletionSource();
        Assert.IsTrue(pendingWorkersById.TryAdd(workerAddr.WorkerId, tcs));
        return WorkerHandle.Start(new WorkerOptions {
            Config = config,
            WorkerAddress = workerAddr,
            ConnectionReady = tcs.Task,
            Observer = extensionContainer.Observer,
            ExtensionContainer = extensionContainer,
        });
    }

    WorkerHandle StartNewInProcess() {
        var workerAddr = WorkerAddress.GetNext(config.Socket);
        return WorkerHandle.InProcess(new WorkerOptions {
            Config = config,
            WorkerAddress = workerAddr,
            ConnectionReady = Task.CompletedTask,
            Observer = extensionContainer.Observer,
            ExtensionContainer = extensionContainer,
        });
    }

    public void Dispose() {
        pool.Dispose();
    }
}
