namespace Trell.IPC.Server;

class WorkerClient {
    readonly WorkerHandle handle;
    readonly Rpc.TrellWorker.TrellWorkerClient rpcClient;

    public WorkerClient(WorkerHandle handle, Rpc.TrellWorker.TrellWorkerClient rpcClient) {
        this.handle = handle;
        this.rpcClient = rpcClient;
    }

    internal async Task<Rpc.WorkResult> ExecuteAsync(Rpc.WorkOrder request) {
        using var _ = this.handle.Track(request);
        return await this.rpcClient.ExecuteAsync(request);
    }

    internal Task<Rpc.ListCurrentExecutionsResult> ListCurrentExecutionsAsync(Google.Protobuf.WellKnownTypes.Empty request) {
        //return await rpcClient.ListCurrentExecutionsAsync(request);
        return this.rpcClient.ListCurrentExecutionsAsync(request).ResponseAsync;
    }

    internal Task<Rpc.CancelWorkerExecutionsResult> CancelWorkerExecutionsAsync(Rpc.CancelWorkerExecutionsRequest request) {
        return this.rpcClient.CancelWorkerExecutionsAsync(request).ResponseAsync;
    }

    internal Task<Rpc.QueryWorkerDbResult> QueryWorkerDb(Rpc.QueryWorkerDbRequest request) {
        return this.rpcClient.QueryWorkerDbAsync(request).ResponseAsync;
    }
}
