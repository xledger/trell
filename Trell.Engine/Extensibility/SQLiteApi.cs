using Newtonsoft.Json;
using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Engine.Utility.Concurrent;
using static Trell.Engine.ClearScriptHelpers.ScriptingInterop;

namespace Trell.Engine.Extensibility {
    using MDS = Microsoft.Data.Sqlite;
    public class SQLiteApi : IPlugin {
        public PluginDotNetObject DotNetObject { get; }

        public SQLiteApi(
            SqliteConnector sharedConnector,
            SqliteConnector? workerConnector,
            IReadOnlyList<string> sharedDbs
        ) {
            this.DotNetObject =
                new PluginDotNetObject((ctx, eng) => new SQLiteApiDotNetObjects.SQLite(
                    sharedConnector,
                    workerConnector,
                    sharedDbs,
                    ctx,
                    eng),
                "dotNetSqlite");
        }

        public string JsScript => """
            sqlite = {
                open: async (options) => {
                    let rawConn = await dotNetSqlite.Open(options ?? {});
                    let validateSqlArgs = (args) => {
                        if (args == null) {
                            throw new Error("args cannot be null");
                        }

                        let sql = args.sql;
                        if (!(typeof sql === 'string' || sql instanceof String)) {
                            throw new Error("Missing required [string] property 'sql'");
                        }

                        let parameters = args.parameters;
                        if (!(parameters == null || typeof parameters === 'object')) {
                            throw new Error("args.parameters must be an object");
                        }
                    };
                    return {
                        exec: async (args) => {
                            validateSqlArgs(args);
                            await rawConn.ExecAsync(args.sql, args.transaction, args.parameters);
                        },
                        query: async(args) => {
                            validateSqlArgs(args);
                            let resultJson = await rawConn.QueryAsync(args.sql, args.transaction, args.parameters);
                            return JSON.parse(resultJson);
                        },
                        queryOne: async(args) => {
                            validateSqlArgs(args);
                            let resultJson = await rawConn.QueryOneAsync(args.sql, args.transaction, args.parameters);
                            return JSON.parse(resultJson);
                        },
                        beginTransaction: () => {
                            let rawTx = rawConn.BeginTransaction();
                            return {
                                commit: () => { rawTx.Commit(); },
                                rollback: () => { rawTx.Rollback(); }
                            };
                        },
                        close: () => {
                            rawConn.Dispose();
                        }
                    };
                }
            };
            """;

        public IReadOnlyList<string> TopLevelJsNamesExposed { get; } = new[] { "sqlite" };
    }

    ///<summary>These</summary>
    namespace SQLiteApiDotNetObjects {
        public class SQLite {
            IAtomRead<TrellExecutionContext> Context { get; }
            SqliteConnector SharedConnector { get; }
            SqliteConnector? WorkerConnector { get; }
            IReadOnlyCollection<string> AllowedSharedDbs { get; }

            public SQLite(
                SqliteConnector sharedConnector,
                SqliteConnector? workerConnector,
                IReadOnlyCollection<string> allowedSharedDbs,
                IAtomRead<TrellExecutionContext> context,
                EngineWrapper _eng
            ) {
                this.SharedConnector = sharedConnector;
                this.WorkerConnector = workerConnector;
                this.AllowedSharedDbs = new HashSet<string>(allowedSharedDbs);
                this.Context = context;

                // If worker connector is not defined and we need it, complain.
                if (this.WorkerConnector is null) {
                    foreach (var dbs in allowedSharedDbs) {
                        if (!dbs.StartsWith("shared/")) {
                            throw new ArgumentNullException(nameof(workerConnector));
                        }
                    }
                }
            }

            public async Task<SQLiteConn> Open(dynamic options = null) {
                var context = this.Context.Value!;
                context.CancellationToken.ThrowIfCancellationRequested();
                var dbName = options?.dbname as string ?? "default";
                var isSharedDb = dbName.StartsWith("shared/");

                if (isSharedDb && !this.AllowedSharedDbs.Contains(dbName)) {
                    throw new TrellUserException(
                        new TrellError(TrellErrorCode.UNAUTHORIZED_DATABASE_ACCESS, dbName));
                }

                SqliteConnector connector;
                if (isSharedDb) {
                    connector = this.SharedConnector;
                    dbName = dbName["shared/".Length..];
                } else {
                    connector = this.WorkerConnector!;
                }

                var conn = await connector.Open(dbName, new SqliteConnectionOptions(false));
                return new SQLiteConn(context, conn);
            }
        }

        public class SQLiteConn : IDisposable, IAsyncDisposable {
            TrellExecutionContext Context { get; }
            MDS.SqliteConnection Conn { get; }

            public SQLiteConn(TrellExecutionContext context, MDS.SqliteConnection conn) {
                this.Context = context;
                this.Conn = conn;
            }

            void AddParameters(MDS.SqliteCommand cmd, dynamic parameters) {
                if (parameters is IDictionary<string, object?> paramDict) {
                    foreach ((var k, var v) in paramDict) {
                        cmd.Parameters.AddWithValue(k, v ?? Convert.DBNull);
                    }
                } else if (!IsNullLike(parameters)) {
                    throw new ArgumentException($"Unsupported parameter type \"{parameters.GetType().Name}\"");
                }
            }

            public Task ExecAsync(string sql,
                object? transaction = null,
                dynamic? parameters = null,
                dynamic? options = null) {
                this.Context.CancellationToken.ThrowIfCancellationRequested();

                using var cmd = this.Conn.CreateCommand();
                cmd.CommandText = sql;
                if (transaction is MDS.SqliteTransaction tx) {
                    cmd.Transaction = tx;
                }
                AddParameters(cmd, parameters);
                return cmd.ExecuteNonQueryAsync();
            }


            /// <summary>
            /// Query and return results as JSON array of objects. (JSON for faster transfer speed).
            /// </summary>
            /// <param name="sql"></param>
            /// <param name="tx"></param>
            /// <param name="parameters"></param>
            /// <param name="options"></param>
            /// <returns></returns>
            public async Task<string> QueryAsync(string sql,
                object? transaction = null,
                dynamic? parameters = null) {
                this.Context.CancellationToken.ThrowIfCancellationRequested();

                using (var cmd = this.Conn.CreateCommand()) {
                    cmd.CommandText = sql;
                    if (transaction is MDS.SqliteTransaction tx) {
                        cmd.Transaction = tx;
                    }
                    AddParameters(cmd, parameters);
                    using (var rdr = await cmd.ExecuteReaderAsync()) {
                        if (!rdr.Read()) {
                            return "null";
                        } else {
                            var sw = new StringWriter();
                            var js = new JsonSerializer();
                            var jw = new JsonTextWriter(sw);
                            var fieldCount = rdr.FieldCount;
                            var fieldNames = Enumerable.Range(0, fieldCount).Select(rdr.GetName).ToArray();
                            jw.WriteStartArray();

                            do {
                                jw.WriteStartObject();
                                for (var i = 0; i < fieldCount; i++) {
                                    var name = fieldNames[i];
                                    jw.WritePropertyName(name);
                                    var v = rdr.GetValue(i);
                                    if (Convert.IsDBNull(v)) {
                                        jw.WriteNull();
                                    } else {
                                        switch (v) {
                                            case long:
                                            case double:
                                            case float:
                                            case decimal:
                                                // Stop JSON.parse from corrupting this data
                                                // @FIXME: This approach has some issues. Is there a better way?
                                                // Possible alternatives
                                                //  https://github.com/josdejong/lossless-json
                                                //  https://github.com/cognitect/transit-js
                                                js.Serialize(jw, v.ToString());
                                                break;
                                            default:
                                                js.Serialize(jw, v);
                                                break;
                                        }
                                    }
                                }
                                jw.WriteEndObject();

                            } while (await rdr.ReadAsync());

                            jw.WriteEndArray();
                            return sw.ToString();
                        }
                    }
                }
            }

            /// <summary>
            /// Query and return results as JSON array of objects. (JSON for faster transfer speed).
            /// </summary>
            /// <param name="sql"></param>
            /// <param name="tx"></param>
            /// <param name="parameters"></param>
            /// <param name="options"></param>
            /// <returns></returns>
            public async Task<string> QueryOneAsync(
                string sql,
                object? transaction = null,
                dynamic? parameters = null) {
                this.Context.CancellationToken.ThrowIfCancellationRequested();

                using (var cmd = this.Conn.CreateCommand()) {
                    cmd.CommandText = sql;
                    if (transaction is MDS.SqliteTransaction tx) {
                        cmd.Transaction = tx;
                    }
                    AddParameters(cmd, parameters);
                    using (var rdr = await cmd.ExecuteReaderAsync()) {
                        if (!rdr.Read()) {
                            return "null";
                        } else {
                            var sw = new StringWriter();
                            var js = new JsonSerializer();
                            var jw = new JsonTextWriter(sw);
                            var fieldCount = rdr.FieldCount;
                            var fieldNames = Enumerable.Range(0, fieldCount).Select(rdr.GetName).ToArray();

                            jw.WriteStartObject();
                            for (var i = 0; i < fieldCount; i++) {
                                var name = fieldNames[i];
                                jw.WritePropertyName(name);
                                var v = rdr.GetValue(i);
                                if (Convert.IsDBNull(v)) {
                                    jw.WriteNull();
                                } else {
                                    js.Serialize(jw, v);
                                }
                            }
                            jw.WriteEndObject();

                            return sw.ToString();
                        }
                    }
                }
            }

            public MDS.SqliteTransaction BeginTransaction() {
                return this.Conn.BeginTransaction();
            }

            public void Dispose() {
                ((IDisposable)this.Conn).Dispose();
                GC.SuppressFinalize(this);
            }

            public ValueTask DisposeAsync() {
                GC.SuppressFinalize(this);
                return ((IAsyncDisposable)this.Conn).DisposeAsync();
            }
        }
    }
}
