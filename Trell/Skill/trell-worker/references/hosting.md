# Hosting Trell

Read this when you configure `Trell.toml`, run `trell serve`, or write an application that sends work to Trell over gRPC.

## Contents

- [Trell.toml](#trelltoml)
- [Running the server](#running-the-server)
- [Storage layout](#storage-layout)
- [Sending work: the Execute call](#sending-work-the-execute-call)
- [Other server calls](#other-server-calls)
- [Extensions](#extensions)

## Trell.toml

```toml
socket = "server.sock"              # Unix domain socket the server listens on

[logger]
type = "Trell.ConsoleLogger"        # required; receives worker console output

[storage]
path = "/var/lib/trell"             # root for all code and data; default "."
data_path = "data/"                 # used by `trell run` and `trell run-file` only
max_database_page_count = 1024      # SQLite page cap per database

[worker.pool]
size = 8                            # maximum worker processes; default: CPU count
pending = 1                         # processes started ahead of demand
single_process = false              # true runs workers inside the server process

[worker.limits]
max_startup_duration = "1s"
max_execution_duration = "15m"
grace_period = "10s"
```

- Durations accept `500ms`, `10s`, `15m`, or a .NET `TimeSpan` string such as `01:30:00`. There is no hour suffix. A missing limit means no limit.
- Serilog settings go under `[Serilog]` tables, in the same shape as `appsettings.json`.
- `trell init` writes a minimal file suitable for local development.

## Running the server

```sh
trell serve --config /etc/trell/Trell.toml --log-level Information
```

The server listens on two endpoints, both gRPC over HTTP/2 without TLS: the Unix socket named by `socket`, and TCP port 3000 on every interface. **The server has no authentication.** Any client that reaches port 3000 can run workers, query their databases, and delete their files, so block that port with a firewall or reach the server only through the socket.

The server starts worker processes by launching its own executable with the hidden `worker` command. Each distinct `userId` is bound to one worker process. Once `worker.pool.size` distinct users have sent work, Trell rejects work from any further user with the error `Too much work.` until the server restarts.

The `.proto` files that define the API are in the Trell repository under `Trell/Protos/`: `TrellServer.proto`, `Work.proto`, and `Shared.proto`. Generate a client from them with your language's gRPC tooling.

## Storage layout

All paths are relative to `storage.path`. Unless a work order overrides them:

```
users/<userId>/
    data/                          shared databases: shared/<name> → <name>.db
    workers/<workerId>/
        src/worker.js              the worker's code, and any modules it imports
        data/<name>.db             the worker's own databases
```

Trell does not deploy code. The host application writes the worker's files into `src/` before sending work.

**Path rules:** every path Trell builds, including the `userId`, the `workerId`, and the entry file name, may contain only lowercase letters, digits, `_`, `.`, and `/`. Trell lowercases the path first and rejects `..`, a leading `/`, and empty segments. A `userId` or `workerId` containing a hyphen, such as a GUID, fails with `InvalidPath`. Map such IDs to an allowed form, for example by removing the hyphens.

## Sending work: the Execute call

`Execute(ServerWorkOrder { workOrder })` runs one handler and returns a `WorkResult`.

`WorkOrder` fields:

| Field | Meaning |
|---|---|
| `user.userId` | Selects the worker process and the `users/<userId>/` folder. |
| `user.data` | Key/value pairs describing the user. They appear in `ListCurrentExecutions` and metrics; the worker does not see them. |
| `executionId` | Your ID for this run. It appears in log lines. |
| `metadata` | Opaque bytes returned by `ListCurrentExecutions`. |
| `workload.workerId` | Selects `workers/<workerId>/`. |
| `workload.function` | Exactly one of `onCronTrigger { cron, timestamp }`, `onRequest { url, method, headers, body }`, `onUpload { filename, type, content }`, or `dynamic { name, arguments }`. `dynamic` calls the export called `name` with `context.argv = arguments`. |
| `workload.data` | `{ text: "<JSON object>" }`, spread into the handler's `context`. |
| `workload.env` | `{ text: "<JSON object>" }`, available as `context.env`. Put secrets here, not in the code. |
| `workload.codePath` | Overrides `users/<userId>/workers/<workerId>/src/`. |
| `workload.dataPath` | Overrides `users/<userId>/workers/<workerId>/data`. |
| `workload.workerFilename` | Overrides `worker.js`. |
| `limits` | `maxStartup`, `maxExecution`, `maxGrace` as protobuf `Duration`s. Trell uses the shorter of each request limit and the matching server limit. |
| `sharedDatabases` | Shared databases the worker may open, written `shared/<name>`. |
| `sharedDatabasesPath` | Overrides `users/<userId>/data`. |

`WorkResult` fields: `code` (`Success`, `UserException`, `Timeout`, `InvalidPath`, `PermissionError`, `InvalidRequest`, `EntryPointNotDefined`, `UnauthorizedDatabaseAccess`), `message` (the handler's return value as JSON on success, or the error message), `stacktrace` (the .NET stack), and `scriptError` (the JavaScript error with its stack).

## Other server calls

- `ListCurrentExecutions()` returns the running executions grouped by `userId`, each with its `executionId`, `metadata`, and user data.
- `DeleteWorkerAndCancelExecutions(user, workerId)` cancels the worker's running executions and **deletes the whole `workers/<workerId>/` folder, code and data included**.
- `QueryWorkerDb(user, workerId, db, query, params, resultAbbreviationLimit, timeout)` runs a read-only query against a worker's database in the default data folder, ignoring any `dataPath` override, and returns columns and typed rows. Always set both `resultAbbreviationLimit` and `timeout`. With no limit Trell returns zero rows and marks the result abbreviated, and with no timeout the call fails.
- `Echo(messages)` returns the messages reversed, for checking connectivity.

## Extensions

`[logger]`, `[[observers]]`, and `[[plugins]]` entries load .NET types by name:

```toml
[logger]
asm = "/opt/trell/ext/Trell.Logger.Redis.dll"   # omit to load from Trell itself
type = "Trell.Logger.Redis.RedisLogger"
config = { configuration = "localhost:6379", stream = "trell_logs" }
```

- A logger implements `ITrellLogger` and receives every `console` message from workers.
- An observer implements `ITrellObserver` and receives startup, shutdown, job start/stop, and metrics events.
- A plugin implements `IPlugin` and adds a .NET object plus a JavaScript shim to every engine, which is how a host gives workers new globals.

The extension class needs either a constructor with no parameters or one that takes `IReadOnlyDictionary<string, string>`, which receives `config`.
