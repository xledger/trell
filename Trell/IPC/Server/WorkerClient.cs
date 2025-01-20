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
        using var _ = this.handle.Track(request);
        return await this.rpcClient.ExecuteAsync(request);
    }

    public Task<Rpc.ListCurrentExecutionsResult> ListCurrentExecutionsAsync(Google.Protobuf.WellKnownTypes.Empty request) {
        return this.rpcClient.ListCurrentExecutionsAsync(request).ResponseAsync;
    }

    public Task<Rpc.CancelWorkerExecutionsResult> CancelWorkerExecutionsAsync(Rpc.CancelWorkerExecutionsRequest request) {
        return this.rpcClient.CancelWorkerExecutionsAsync(request).ResponseAsync;
    }

    public Task<Rpc.QueryWorkerDbResult> QueryWorkerDbAsync(Rpc.QueryWorkerDbRequest request) {
        return this.rpcClient.QueryWorkerDbAsync(request).ResponseAsync;
    }
}
