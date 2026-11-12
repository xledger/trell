using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.ClearScript;
using Serilog;
using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Rpc;
using static Trell.Rpc.ToEngine;

namespace Trell.IPC.Worker;

sealed class TrellWorkerImpl : Rpc.TrellWorker.TrellWorkerBase, IDisposable {
    readonly TrellWorkerCore worker;

    public TrellWorkerImpl(
        TrellConfig config,
        TrellExtensionContainer extensionContainer,
        RuntimeWrapper runtime
    ) {
        worker = new TrellWorkerCore(config, extensionContainer, runtime);
    }

    public override Task<Rpc.WorkResult> Execute(Rpc.WorkOrder request, ServerCallContext context) {
        return worker.ExecuteAsync(request);
    }

    public override Task<Rpc.QueryWorkerDbResult> QueryWorkerDb(
        Rpc.QueryWorkerDbRequest request,
        ServerCallContext _context
    ) {
        return worker.QueryWorkerDbAsync(request);
    }

    public override Task<Rpc.ListCurrentExecutionsResult> ListCurrentExecutions(
        Empty request,
        ServerCallContext _context
    ) {
        return worker.ListCurrentExecutionsAsync(request);
    }

    public override Task<Rpc.CancelWorkerExecutionsResult> CancelWorkerExecutions(
        Rpc.CancelWorkerExecutionsRequest request,
        ServerCallContext _context
    ) {
        return worker.CancelWorkerExecutionsAsync(request);
    }

    public void Dispose() {
        worker.Dispose();
    }
}
