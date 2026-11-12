using Google.Protobuf.WellKnownTypes;
using Trell.Rpc;

namespace Trell.IPC.Server;

class WorkerClient : IWorkerClient {
    readonly WorkerHandle handle;
    readonly Rpc.TrellWorker.TrellWorkerClient rpcClient;

    public WorkerClient(WorkerHandle handle, Rpc.TrellWorker.TrellWorkerClient rpcClient) {
        this.handle = handle;
        this.rpcClient = rpcClient;
    }

    public async Task<Rpc.WorkResult> ExecuteAsync(Rpc.WorkOrder request) {
        using var _ = handle.Track(request);
        return await rpcClient.ExecuteAsync(request);
    }

    public Task<Rpc.ListCurrentExecutionsResult> ListCurrentExecutionsAsync(
        Google.Protobuf.WellKnownTypes.Empty request
    ) {
        return rpcClient.ListCurrentExecutionsAsync(request).ResponseAsync;
    }

    public Task<Rpc.CancelWorkerExecutionsResult> CancelWorkerExecutionsAsync(
        Rpc.CancelWorkerExecutionsRequest request
    ) {
        return rpcClient.CancelWorkerExecutionsAsync(request).ResponseAsync;
    }

    public Task<Rpc.QueryWorkerDbResult> QueryWorkerDbAsync(Rpc.QueryWorkerDbRequest request) {
        return rpcClient.QueryWorkerDbAsync(request).ResponseAsync;
    }
}
