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
            pending: config.Worker.Pool.Pending,
            idleTimeout: config.Worker.Pool.IdleTimeout
        );
        if (config.Worker.Pool.SingleProcess) {
            Log.Information(
                "Started worker pool in the current process: size {Size}, pending {Pending}, "
                    + "idle timeout {IdleTimeout}.",
                config.Worker.Pool.Size,
                config.Worker.Pool.Pending,
                config.Worker.Pool.IdleTimeout
            );
        } else {
            Log.Information(
                "Started worker pool: size {Size}, pending {Pending}, idle timeout {IdleTimeout}.",
                config.Worker.Pool.Size,
                config.Worker.Pool.Pending,
                config.Worker.Pool.IdleTimeout
            );
        }
    }

    public IEnumerable<WorkerHandle> Handles => pool.Values;

    /// <summary>
    /// Returns a lease on the worker process bound to userId, binding one if needed. The user
    /// keeps the process until the lease is disposed and the pool's idle timeout passes.
    /// </summary>
    internal BoundedObjectPool<string, WorkerHandle>.Lease AcquireWorkerHandle(string userId) {
        if (!pool.TryAcquire(userId, out var lease)) {
            throw new Exception("Too much work.");
        }

        return lease;
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
