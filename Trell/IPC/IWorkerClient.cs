using Google.Protobuf.WellKnownTypes;
using Trell.Rpc;

namespace Trell.IPC;

interface IWorkerClient {
    Task<CancelWorkerExecutionsResult> CancelWorkerExecutionsAsync(CancelWorkerExecutionsRequest request);

    Task<WorkResult> ExecuteAsync(WorkOrder request);

    Task<ListCurrentExecutionsResult> ListCurrentExecutionsAsync(Empty request);

    Task<QueryWorkerDbResult> QueryWorkerDbAsync(QueryWorkerDbRequest request);
}
