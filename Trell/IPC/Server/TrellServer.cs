using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Trell.Engine.Extensibility;
using Trell.Rpc;

namespace Trell.IPC.Server;

public class TrellServer : Rpc.TrellServer.TrellServerBase {
    TrellConfig Config { get; }
    readonly TrellExtensionContainer extensionContainer;

    readonly WorkerPool pool;

    public TrellServer(TrellConfig config, TrellExtensionContainer extensionContainer) {
        this.Config = config;
        this.extensionContainer = extensionContainer;
        this.pool = new WorkerPool(config, extensionContainer);
    }

    public override async Task<WorkResult> Execute(ServerWorkOrder request, ServerCallContext context) {
        var handle = this.pool.GetWorkerHandle(request.WorkOrder.User.UserId);
        var worker = await handle.GetClientAsync();
        return await worker.ExecuteAsync(request.WorkOrder);
    }

    public override async Task<QueryWorkerDbResult> QueryWorkerDb(QueryWorkerDbRequest request, ServerCallContext context) {
        var handle = this.pool.GetWorkerHandle(request.User.UserId);
        var worker = await handle.GetClientAsync();
        return await worker.QueryWorkerDbAsync(request);
    }

    public override Task<EchoResult> Echo(EchoRequest request, ServerCallContext context) {
        var messages = request.Messages.Reverse().Select(m => string.Join("", m.Reverse()));
        var result = new EchoResult { Messages = { messages } };
        return Task.FromResult(result);
    }

    public override async Task<ListCurrentExecutionsResult> ListCurrentExecutions(Empty request, ServerCallContext context) {
        var listTasks = this.pool.Handles.Select(async handle => {
            try {
                if (handle.IsAvailable) {
                    var worker = await handle.GetClientAsync();
                    return await worker.ListCurrentExecutionsAsync(request);
                } else {
                    return new ListCurrentExecutionsResult();
                }
            } catch (Exception) {
                // When some exceptions happen in the call to the worker,
                // don't want to propagate that out of this call. Assume
                // that the worker is not executing any jobs or has shutdown.
                return null;
            }
        }).ToList();
        var workerResults = await Task.WhenAll(listTasks);

        var results = new ListCurrentExecutionsResult();
        foreach (var workerResult in workerResults) {
            if (workerResult is null) {
                continue;
            }

            foreach (var (userId, execs) in workerResult.ExecutionsByUserId) {
                if (!results.ExecutionsByUserId.TryGetValue(userId, out var descriptors)) {
                    results.ExecutionsByUserId[userId] = descriptors = new();
                }

                descriptors.Descriptors.AddRange(execs.Descriptors);
            }
        }
        return results;
    }

    public override async Task<DeleteWorkerAndCancelExecutionsResult> DeleteWorkerAndCancelExecutions(DeleteWorkerAndCancelExecutionsRequest request, ServerCallContext context) {
        var r = new DeleteWorkerAndCancelExecutionsResult();
        var cancelExecutionsRequest = new CancelWorkerExecutionsRequest() { WorkerId = request.WorkerId };

        foreach (var handle in this.pool.Handles) {
            try {
                if (handle.IsAvailable) {
                    var worker = await handle.GetClientAsync();
                    foreach (var executionId in (await worker.CancelWorkerExecutionsAsync(cancelExecutionsRequest)).CancelledExecutionIds) {
                        r.CancelledExecutionIds.Add(executionId);
                    }
                }
            } catch (Exception) {
                // copied pattern from above (ListCurrentExecutions), if an exception occurs
                // assume the worker isn't executing anything
            }
        }

        if (!this.extensionContainer.Storage!.TryResolveTrellPath($"users/{request.User.UserId}/workers/{request.WorkerId}", out var dir, out var error)) {
            throw new TrellUserException(error);
        }

        // Delete the worker's data directory
        if (Directory.Exists(dir.ToString())) {
            // Remove ReadOnly flag from files so we can delete them
            foreach (var filePath in Directory.EnumerateFiles(dir.ToString(), "*", SearchOption.AllDirectories)) {
                var attrs = File.GetAttributes(filePath);
                if (attrs.HasFlag(FileAttributes.ReadOnly)) {
                    File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
                }
            }
            Directory.Delete(dir, true);
        }

        return r;
    }

    public override Task<Empty> NotifyWorkerReady(WorkerReady request, ServerCallContext context) {
        if (!this.pool.TrySetWorkerReady(request.WorkerId)) {
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, "WorkerId not found."));
        }
        Serilog.Log.Information("Worker {Id} is ready.", request.WorkerId);
        return Task.FromResult(MessageConstants.Empty);
    }

    /// <summary>
    /// Called from the worker (on a user's behalf) to log.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    /// <exception cref="RpcException"></exception>
    public override Task<Empty> Log(LogRequest request, ServerCallContext context) {
        throw new NotImplementedException();
    }
}
