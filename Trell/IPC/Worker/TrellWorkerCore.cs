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
using static Trell.Rpc.ToEngine;

namespace Trell.IPC.Worker;

sealed class TrellWorkerCore : IDisposable, IWorkerClient {
    /// <summary>
    /// How long QueryWorkerDb lets a query run when the request does not set a timeout.
    /// </summary>
    internal static readonly TimeSpan DEFAULT_QUERY_TIMEOUT = TimeSpan.FromSeconds(30);

    readonly TrellConfig config;
    readonly TrellExtensionContainer extensionContainer;
    readonly DateTime createdAt;
    readonly RuntimeWrapper runtime;

    readonly ConcurrentDictionary<string, ReadOnlyMemory<byte[]>> execIdToMetadata = new();
    readonly ConcurrentDictionary<Rpc.WorkOrder, object?> currentExecs = new();

    // The running executions of each worker, so CancelWorkerExecutionsAsync can cancel them
    // together. ctsLock guards both levels. The last execution of a worker to finish removes
    // the worker's inner dictionary, so the outer one holds only workers that are running.
    readonly Lock ctsLock = new();
    readonly Dictionary<
        string,
        Dictionary<Rpc.WorkOrder, CancellationTokenSource>
    > workerIdToWorkOrderToCts = new();

    public TrellWorkerCore(
        TrellConfig config,
        TrellExtensionContainer extensionContainer,
        RuntimeWrapper runtime
    ) {
        this.config = config;
        this.extensionContainer = extensionContainer;
        this.runtime = runtime;
        createdAt = DateTime.UtcNow;
    }

    SqliteConnector Connector(IStorageProvider storage, string path) {
        if (storage.TryScopeToSubdirectory(path, out var dbStorage, out var error)) {
            return new SqliteConnector(dbStorage);
        }

        throw new TrellUserException(error);
    }

    public async Task<Rpc.WorkResult> ExecuteAsync(Rpc.WorkOrder request) {
        Log.Information("In TrellWorker Execute");
        currentExecs.TryAdd(request, null);

        try {
            var plugins = new List<IPlugin>();

            if (extensionContainer.Storage != null) {
                var sharedConnector = Connector(
                    extensionContainer.Storage,
                    string.IsNullOrEmpty(request.SharedDatabasesPath)
                        ? $"users/{request.User.UserId}/data"
                        : request.SharedDatabasesPath
                );

                var workerConnector = Connector(
                    extensionContainer.Storage,
                    string.IsNullOrEmpty(request.Workload.DataPath)
                        ? $"users/{request.User.UserId}/workers/{request.Workload.WorkerId}/data"
                        : request.Workload.DataPath
                );

                plugins.Add(
                    new SQLiteApi(
                        sharedConnector,
                        workerConnector,
                        request.SharedDatabases.ToList()
                    )
                );
            }
            using var engine = runtime.CreateScriptEngine(plugins);
            using var cts = new CancellationTokenSource();

            TrackExecution(request, cts);

            var executionContext = request.ToExecutionContext(cts.Token);
            var work = request.ToWork(extensionContainer.Storage, engine);

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
                    || cts.IsCancellationRequested
                ) {
                    workResult.Code = Rpc.ResultCode.Timeout;
                } else if (ex is TrellUserException uex) {
                    workResult.Code = uex.Error.Code switch {
                        TrellErrorCode.INVALID_PATH => Rpc.ResultCode.InvalidPath,
                        TrellErrorCode.PERMISSION_ERROR => Rpc.ResultCode.PermissionError,
                        TrellErrorCode.ENTRY_POINT_NOT_DEFINED => Rpc.ResultCode.EntryPointNotDefined,
                        TrellErrorCode.TIMEOUT => Rpc.ResultCode.Timeout,
                        TrellErrorCode.UNAUTHORIZED_DATABASE_ACCESS => Rpc
                            .ResultCode
                            .UnauthorizedDatabaseAccess,
                        _ => Rpc.ResultCode.UserException,
                    };
                } else {
                    workResult.Code = Rpc.ResultCode.UserException;
                }
                workResult.Message = ex.Message;
                workResult.Stacktrace = ex.StackTrace;
                if (ex is ScriptEngineException scriptEx) {
                    workResult.ScriptError = scriptEx.ErrorDetails;
                }
            }

            return workResult;
        } finally {
            currentExecs.Remove(request, out _);
            UntrackExecution(request);
        }
    }

    // Inner dictionaries compare work orders by reference, so two equal work orders running at
    // once are tracked, canceled, and untracked separately.
    void TrackExecution(Rpc.WorkOrder request, CancellationTokenSource cts) {
        lock (ctsLock) {
            var workerId = request.Workload.WorkerId;
            if (!workerIdToWorkOrderToCts.TryGetValue(workerId, out var workOrderToCts)) {
                workOrderToCts = new(ReferenceEqualityComparer.Instance);
                workerIdToWorkOrderToCts[workerId] = workOrderToCts;
            }
            workOrderToCts[request] = cts;
        }
    }

    void UntrackExecution(Rpc.WorkOrder request) {
        lock (ctsLock) {
            var workerId = request.Workload.WorkerId;
            if (workerIdToWorkOrderToCts.TryGetValue(workerId, out var workOrderToCts)
                && workOrderToCts.Remove(request)
                && workOrderToCts.Count == 0
            ) {
                workerIdToWorkOrderToCts.Remove(workerId);
            }
        }
    }

    /// <summary>
    /// The number of workers that have at least one execution running.
    /// </summary>
    internal int TrackedWorkerCount {
        get {
            lock (ctsLock) {
                return workerIdToWorkOrderToCts.Count;
            }
        }
    }

    public async Task<Rpc.QueryWorkerDbResult> QueryWorkerDbAsync(Rpc.QueryWorkerDbRequest request) {
        if (extensionContainer.Storage == null) {
            throw new InvalidOperationException();
        }

        var connector = Connector(
            extensionContainer.Storage,
            $"users/{request.User.UserId}/workers/{request.WorkerId}/data"
        );
        using var conn = await connector.Open(
            request.HasDb ? request.Db : "default",
            new SqliteConnectionOptions(true)
        );
        using var cmd = conn.CreateCommand();
        cmd.CommandText = request.Query;
        cmd.CommandType = CommandType.Text;

        // The request's parameters have no names, so each one binds by position: the first
        // binds to ?1, the second to ?2, and so on. Microsoft.Data.Sqlite binds parameters
        // by name, and SQLite gives a bare ? no name, so a query that uses a bare ? fails.
        for (var i = 0; i < request.Params.Count; i++) {
            var p = request.Params[i];
            var param = cmd.CreateParameter();
            param.ParameterName = $"?{i + 1}";
            param.Value = p.ValueCase switch {
                Rpc.DbValue.ValueOneofCase.Bool => p.Bool,
                Rpc.DbValue.ValueOneofCase.Double => p.Double,
                Rpc.DbValue.ValueOneofCase.Int32 => p.Int32,
                Rpc.DbValue.ValueOneofCase.Int64 => p.Int64,
                Rpc.DbValue.ValueOneofCase.String => p.String,
                Rpc.DbValue.ValueOneofCase.Bytes => p.Bytes.ToArray(),
                _ => throw new NotImplementedException(),
            };
            cmd.Parameters.Add(param);
        }

        // Callers may leave the row limit and the timeout unset. An unset limit returns every row;
        // an unset timeout uses DEFAULT_QUERY_TIMEOUT.
        var rowLimit = request.HasResultAbbreviationLimit ? request.ResultAbbreviationLimit : int.MaxValue;
        var timeout = request.Timeout?.ToTimeSpan() ?? DEFAULT_QUERY_TIMEOUT;

        try {
            using var cts = new CancellationTokenSource(timeout);
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
                if (result.Rows.Count >= rowLimit) {
                    result.IsAbbreviated = true;
                    break;
                }

                var row = new Rpc.DbRow();
                foreach (var column in columns) {
                    var value = reader.GetValue(
                        column.ColumnOrdinal ?? throw new NotImplementedException()
                    );
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
                        _ => throw new NotImplementedException(
                            $"Type {value.GetType().Name} not implemented."
                        ),
                    });
                }

                result.Rows.Add(row);
            }

            return new Rpc.QueryWorkerDbResult { Data = result };
        } catch (Exception ex)
            when (ex is OperationCanceledException || ex.Message == "SQLite Error 9: 'interrupted'."
            ) {
            return new Rpc.QueryWorkerDbResult {
                Error = new Rpc.QueryWorkerDbErrorResult { Code = "timeout", Msg = ex.Message, },
            };
        } catch (Exception ex) {
            return new Rpc.QueryWorkerDbResult {
                Error = new Rpc.QueryWorkerDbErrorResult { Code = "unknown", Msg = ex.Message, },
            };
        }
    }

    public Task<Rpc.ListCurrentExecutionsResult> ListCurrentExecutionsAsync(Empty request) {
        var current = new Rpc.ListCurrentExecutionsResult();

        foreach (var work in currentExecs.Keys) {
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

    public Task<Rpc.CancelWorkerExecutionsResult> CancelWorkerExecutionsAsync(
        Rpc.CancelWorkerExecutionsRequest request
    ) {
        // Copy the executions under the lock and cancel them outside it. Cancel runs callbacks
        // inline, and those can finish an execution, which then untracks itself.
        KeyValuePair<Rpc.WorkOrder, CancellationTokenSource>[] executions;
        lock (ctsLock) {
            executions =
                workerIdToWorkOrderToCts.TryGetValue(request.WorkerId, out var workOrderToCts)
                    ? workOrderToCts.ToArray()
                    : [];
        }

        var r = new Rpc.CancelWorkerExecutionsResult();
        foreach (var (work, cts) in executions) {
            try {
                cts.Cancel();
            } catch (ObjectDisposedException) {
                // The execution finished after the copy was made, so there is nothing to cancel.
                continue;
            }
            r.CancelledExecutionIds.Add(work.ExecutionId);
        }
        return Task.FromResult(r);
    }

    static async Task<object?> CancelAfterAsync(CancellationTokenSource cts, TimeSpan timeout) {
        await Task.Delay(timeout, cts.Token);
        cts.Cancel();
        throw new OperationCanceledException();
    }

    public void Dispose() {
        runtime.Dispose();
    }
}
