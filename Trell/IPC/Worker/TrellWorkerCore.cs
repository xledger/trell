using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.ClearScript;
using Serilog;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data;
using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Rpc;
using static Trell.Rpc.ToEngine;

namespace Trell.IPC.Worker;

sealed class TrellWorkerCore : IDisposable, IWorkerClient {
    readonly TrellConfig config;
    readonly TrellExtensionContainer extensionContainer;
    readonly DateTime createdAt;
    readonly RuntimeWrapper runtime;

    readonly ConcurrentDictionary<string, ReadOnlyMemory<byte[]>> execIdToMetadata = new();
    readonly ConcurrentDictionary<Rpc.WorkOrder, object?> currentExecs = new();
    readonly ConcurrentDictionary<string, ConcurrentDictionary<Rpc.WorkOrder, CancellationTokenSource>> workerIdToWorkOrderToCts = new();

    public TrellWorkerCore(TrellConfig config, TrellExtensionContainer extensionContainer, RuntimeWrapper runtime) {
        this.config = config;
        this.extensionContainer = extensionContainer;
        this.runtime = runtime;
        this.createdAt = DateTime.UtcNow;
    }

    SqliteConnector Connector(IStorageProvider storage, string path) {
        if (storage.TryWithRoot(path, out var dbStorage, out var error)) {
            return new SqliteConnector(dbStorage);
        }

        throw new TrellUserException(error);
    }

    public async Task<Rpc.WorkResult> ExecuteAsync(Rpc.WorkOrder request) {
        Log.Information("In TrellWorker Execute");
        this.currentExecs.TryAdd(request, null);

        try {
            var plugins = new List<IPlugin>();

            if (this.extensionContainer.Storage != null) {
                var sharedConnector = Connector(
                    this.extensionContainer.Storage,
                    string.IsNullOrEmpty(request.SharedDatabasesPath)
                        ? $"users/{request.User.UserId}/data"
                        : request.SharedDatabasesPath
                );

                var workerConnector = Connector(
                    this.extensionContainer.Storage,
                    string.IsNullOrEmpty(request.Workload.DataPath)
                        ? $"users/{request.User.UserId}/workers/{request.Workload.WorkerId}/data"
                        : request.Workload.DataPath
                );

                plugins.Add(new SQLiteApi(sharedConnector, workerConnector, request.SharedDatabases.ToList()));
            }
            using var engine = this.runtime.CreateScriptEngine(plugins);
            using var cts = new CancellationTokenSource();

            // Keep track of the CancellationTokenSources for workerId executions, so we can cancel them in bulk if requested
            var workOrderToCts = this.workerIdToWorkOrderToCts.GetOrAdd(request.Workload.WorkerId, new ConcurrentDictionary<Rpc.WorkOrder, CancellationTokenSource>());
            workOrderToCts.TryAdd(request, cts);

            var executionContext = request.ToExecutionContext(cts.Token);
            var work = request.ToWork(this.extensionContainer.Storage, engine);

            var workResult = new Rpc.WorkResult();

            try {
                var tasks = new Task<object?>[] {
                    CancelAfterAsync(cts, work.Limits.DurationWithGracePeriod()),
                    engine.RunWorkAsync(executionContext, work),
                };
                var finishedTask = await Task.WhenAny(tasks);
                var result = await finishedTask;
                workResult.Code = Rpc.ResultCode.Success;
                workResult.Message = result?.ToString() ?? "";
            } catch (Exception ex) {
                if (ex is OperationCanceledException or ScriptInterruptedException
                    || cts.IsCancellationRequested) {
                    workResult.Code = Rpc.ResultCode.Timeout;
                } else if (ex is TrellUserException uex) {
                    workResult.Code = uex.Error.Code switch {
                        TrellErrorCode.INVALID_PATH => Rpc.ResultCode.InvalidPath,
                        TrellErrorCode.PERMISSION_ERROR => Rpc.ResultCode.PermissionError,
                        TrellErrorCode.ENTRY_POINT_NOT_DEFINED => Rpc.ResultCode.EntryPointNotDefined,
                        TrellErrorCode.TIMEOUT => Rpc.ResultCode.Timeout,
                        TrellErrorCode.UNAUTHORIZED_DATABASE_ACCESS => Rpc.ResultCode.UnauthorizedDatabaseAccess,
                        _ => Rpc.ResultCode.UserException
                    };
                } else {
                    workResult.Code = Rpc.ResultCode.UserException;
                }
                workResult.Message = ex.Message;
                workResult.Stacktrace = ex.StackTrace;
            }

            return workResult;
        } finally {
            this.currentExecs.Remove(request, out _);
            if (this.workerIdToWorkOrderToCts.TryGetValue(request.Workload.WorkerId, out var workOrderToCts)) {
                // PERF: possible memory leak, doesn't remove inner dictionary when empty, maybe fix later
                workOrderToCts.Remove(request, out var _);
            }
        }
    }

    public async Task<Rpc.QueryWorkerDbResult> QueryWorkerDbAsync(Rpc.QueryWorkerDbRequest request) {
        if (this.extensionContainer.Storage == null) {
            // TODO: what to throw?
            throw new InvalidOperationException();
        }

        var connector = Connector(
            this.extensionContainer.Storage,
            $"users/{request.User.UserId}/workers/{request.WorkerId}/data"
        );
        using var conn = await connector.Open(request.Db ?? "default", new SqliteConnectionOptions(true));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = request.Query;
        cmd.CommandType = CommandType.Text;

        foreach (var p in request.Params) {
            var param = cmd.CreateParameter();
            param.Value = p.ValueCase switch {
                Rpc.DbValue.ValueOneofCase.Bool => p.Bool,
                Rpc.DbValue.ValueOneofCase.Double => p.Double,
                Rpc.DbValue.ValueOneofCase.Int32 => p.Int32,
                Rpc.DbValue.ValueOneofCase.Int64 => p.Int64,
                Rpc.DbValue.ValueOneofCase.String => p.String,
                Rpc.DbValue.ValueOneofCase.Bytes => p.Bytes.ToArray(),
                _ => throw new NotImplementedException()
            };
        }

        try {
            using var cts = new CancellationTokenSource(request.Timeout.ToTimeSpan());
            cts.Token.Register(() => {
                conn.Interrupt();
            });
            using var reader = await cmd.ExecuteReaderAsync(cts.Token);
            var columns = await reader.GetColumnSchemaAsync(cts.Token);
            var result = new Rpc.QueryWorkerDbDataResult();

            foreach (var column in columns) {
                result.Columns.Add(column.ColumnName);
            }

            while (await reader.ReadAsync(cts.Token)) {
                if (result.Rows.Count >= request.ResultAbbreviationLimit) {
                    result.IsAbbreviated = true;
                    break;
                }

                var row = new Rpc.DbRow();
                foreach (var column in columns) {
                    var value = reader.GetValue(column.ColumnOrdinal ?? throw new NotImplementedException());
                    row.Values.Add(value switch {
                        string s => new Rpc.DbValue { String = s },
                        int i => new Rpc.DbValue { Int32 = i },
                        long i => new Rpc.DbValue { Int64 = i },
                        byte[] bs => new Rpc.DbValue { Bytes = Google.Protobuf.ByteString.CopyFrom(bs) },
                        float f => new Rpc.DbValue { Double = f },
                        double d => new Rpc.DbValue { Double = d },
                        bool b => new Rpc.DbValue { Bool = b },
                        char[] cs => new Rpc.DbValue { String = new string(cs) },
                        null => new Rpc.DbValue(),
                        DBNull => new Rpc.DbValue(),
                        _ => throw new NotImplementedException($"Type {value.GetType().Name} not implemented.")
                    });
                }

                result.Rows.Add(row);
            }

            return new Rpc.QueryWorkerDbResult { Data = result };
        } catch (Exception ex) when (ex is OperationCanceledException || ex.Message == "SQLite Error 9: 'interrupted'.") {
            return new Rpc.QueryWorkerDbResult {
                Error = new Rpc.QueryWorkerDbErrorResult {
                    Code = "timeout",
                    Msg = ex.Message
                }
            };
        } catch (Exception ex) {
            return new Rpc.QueryWorkerDbResult {
                Error = new Rpc.QueryWorkerDbErrorResult {
                    Code = "unknown",
                    Msg = ex.Message
                }
            };
        }
    }

    public Task<Rpc.ListCurrentExecutionsResult> ListCurrentExecutionsAsync(Empty request) {
        var current = new Rpc.ListCurrentExecutionsResult();

        foreach (var work in this.currentExecs.Keys) {
            if (!current.ExecutionsByUserId.TryGetValue(work.User!.UserId, out var descriptors)) {
                current.ExecutionsByUserId[work.User.UserId] = descriptors = new();
            }
            descriptors.Descriptors.Add(new Rpc.ExecutionDescriptor() {
                ExecutionId = work.ExecutionId,
                Metadata = work.Metadata,
                UserData = work.User?.Data,
            });
        }

        return Task.FromResult(current);
    }

    public Task<Rpc.CancelWorkerExecutionsResult> CancelWorkerExecutionsAsync(Rpc.CancelWorkerExecutionsRequest request) {
        var r = new Rpc.CancelWorkerExecutionsResult();
        if (this.workerIdToWorkOrderToCts.TryGetValue(request.WorkerId, out var workAndCancellationTokenSourcePairs)) {
            foreach (var (work, cts) in workAndCancellationTokenSourcePairs) {
                cts.Cancel();
                r.CancelledExecutionIds.Add(work.ExecutionId);
            }
        }
        return Task.FromResult(r);
    }

    static async Task<object?> CancelAfterAsync(CancellationTokenSource cts, TimeSpan timeout) {
        await Task.Delay(timeout, cts.Token);
        cts.Cancel();
        // TODO: TrellUserException.Timeout
        throw new OperationCanceledException();
    }

    public void Dispose() {
        this.runtime.Dispose();
    }
}
