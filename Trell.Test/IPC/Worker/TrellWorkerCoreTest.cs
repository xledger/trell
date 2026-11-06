using System.Linq;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Extensibility;
using Trell.IPC.Worker;
using Trell.Test.Engine;

namespace Trell.Test.IPC.Worker;

public class TrellWorkerCoreTest : IDisposable {
    const string USER_ID = "user1";
    const string WORKER_ID = "worker1";
    const int ROW_COUNT = 3;

    readonly string storageDir;
    readonly TrellWorkerCore worker;

    public TrellWorkerCoreTest() {
        storageDir = Directory.CreateTempSubdirectory("trell_worker_core_test_").FullName;
        var dataDir = Path.Join(storageDir, "users", USER_ID, "workers", WORKER_ID, "data");
        Directory.CreateDirectory(dataDir);
        using (var conn = new SqliteConnection($"Data Source={Path.Join(dataDir, "default.db")}")) {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(a INTEGER); INSERT INTO t VALUES (1), (2), (3);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var extensions = new TrellExtensionContainer(
            new DummyLogger(),
            new LocalFolderStorage(storageDir, 1024),
            [],
            []
        );
        var runtime = new RuntimeWrapper(extensions, new RuntimeWrapper.Config());
        worker = new TrellWorkerCore(new TrellConfig(), extensions, runtime);
    }

    public void Dispose() {
        worker.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(storageDir, true);
    }

    static Rpc.QueryWorkerDbRequest Request() => new() {
        User = new() { UserId = USER_ID },
        WorkerId = WORKER_ID,
        Query = "SELECT a FROM t ORDER BY a",
    };

    static Rpc.WorkOrder WorkOrder(string workerId, string executionId) => new() {
        User = new() { UserId = USER_ID },
        ExecutionId = executionId,
        Workload = new() {
            WorkerId = workerId,
            Function = new() { Dynamic = new() { Name = "run" } },
        },
    };

    void WriteSrcFile(string workerId, string relativePath, string contents) {
        var path =
            Path.Join(storageDir, "users", USER_ID, "workers", workerId, "src", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    void WriteWorkerJs(string workerId, string runBody, string fileName = "worker.js") {
        WriteSrcFile(
            workerId,
            fileName,
            $$"""
            async function run(context) {
                {{runBody}}
            }

            export default { run }

            """
        );
    }

    [Fact]
    public async Task FinishedExecutionsLeaveNoWorkersTracked() {
        WriteWorkerJs("a", "return 'done';");
        WriteWorkerJs("b", "return 'done';");

        var results = await Task.WhenAll(
            worker.ExecuteAsync(WorkOrder("a", "exec-a1")),
            worker.ExecuteAsync(WorkOrder("a", "exec-a2")),
            worker.ExecuteAsync(WorkOrder("b", "exec-b1"))
        );

        Assert.All(results, r => Assert.Equal(Rpc.ResultCode.Success, r.Code));
        Assert.Equal(0, worker.TrackedWorkerCount);
    }

    [Fact]
    public async Task CanceledExecutionsLeaveNoWorkersTracked() {
        WriteWorkerJs(WORKER_ID, "await new Promise(() => {});");

        var execution = Task.Run(() => worker.ExecuteAsync(WorkOrder(WORKER_ID, "exec-1")));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (worker.TrackedWorkerCount == 0) {
            Assert.True(DateTime.UtcNow < deadline, "The execution never started.");
            await Task.Delay(10);
        }

        var canceled = await worker.CancelWorkerExecutionsAsync(new() { WorkerId = WORKER_ID });
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["exec-1"], canceled.CancelledExecutionIds);
        Assert.Equal(Rpc.ResultCode.Timeout, result.Code);
        Assert.Equal(0, worker.TrackedWorkerCount);
    }

    [Fact]
    public async Task WorkerFilenameKeepsItsCase() {
        WriteSrcFile(WORKER_ID, "Lib/Helper.js", "export const helper = 'helped';");
        WriteWorkerJs(
            WORKER_ID,
            "const { helper } = await import('./Lib/Helper.js'); return helper;",
            "MyWorker.js"
        );
        var order = WorkOrder(WORKER_ID, "exec-1");
        order.Workload.WorkerFilename = "MyWorker.js";

        var result = await worker.ExecuteAsync(order);

        Assert.Equal(Rpc.ResultCode.Success, result.Code);
        Assert.Equal("\"helped\"", result.Message);
    }

    [Fact]
    public async Task WorkerFilenameMustMatchCaseOnDisk() {
        WriteWorkerJs(WORKER_ID, "return 'done';", "MyWorker.js");
        var order = WorkOrder(WORKER_ID, "exec-1");
        order.Workload.WorkerFilename = "myworker.js";

        var result = await worker.ExecuteAsync(order);

        Assert.NotEqual(Rpc.ResultCode.Success, result.Code);
        Assert.Contains(
            "\"myworker.js\" not found; the file on disk is \"MyWorker.js\"",
            result.Message
        );
    }

    [Fact]
    public async Task ImportMustMatchCaseOnDisk() {
        WriteSrcFile(WORKER_ID, "Utils.js", "export const utils = 1;");
        WriteWorkerJs(WORKER_ID, "await import('./utils.js'); return 'done';");

        var result = await worker.ExecuteAsync(WorkOrder(WORKER_ID, "exec-1"));

        Assert.NotEqual(Rpc.ResultCode.Success, result.Code);
        Assert.Contains(
            "\"./utils.js\" not found; the file on disk is \"Utils.js\"",
            result.Message
        );
    }

    [Fact]
    public async Task DatabaseNamesAreLowercased() {
        var dataDir = Path.Join(storageDir, "users", USER_ID, "workers", WORKER_ID, "data");
        var connector = new SqliteConnector(new LocalFolderStorage(dataDir, 1024));

        // A mixed-case name opens the existing lowercase file.
        using (var conn = await connector.Open("Default", new(ReadOnly: true))) {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM t";
            Assert.Equal((long)ROW_COUNT, await cmd.ExecuteScalarAsync());
        }

        // A new database gets a lowercase file name. Compare the names on disk, because a
        // case-insensitive file system would find "MyDb.db" under either name.
        using (await connector.Open("MyDb", new(ReadOnly: false))) { }
        SqliteConnection.ClearAllPools();
        var names = Directory.EnumerateFiles(dataDir).Select(Path.GetFileName).ToList();
        Assert.Contains("mydb.db", names);
        Assert.DoesNotContain("MyDb.db", names);
    }

    [Fact]
    public async Task QueryWithoutLimitOrTimeoutReturnsEveryRow() {
        var result = await worker.QueryWorkerDbAsync(Request());

        Assert.Equal(Rpc.QueryWorkerDbResult.ResultOneofCase.Data, result.ResultCase);
        Assert.Equal(["a"], result.Data.Columns);
        Assert.Equal(ROW_COUNT, result.Data.Rows.Count);
        Assert.False(result.Data.IsAbbreviated);
    }

    [Fact]
    public async Task QueryWithLimitAbbreviatesResult() {
        var request = Request();
        request.ResultAbbreviationLimit = 2;
        request.Timeout = Duration.FromTimeSpan(TimeSpan.FromSeconds(5));

        var result = await worker.QueryWorkerDbAsync(request);

        Assert.Equal(Rpc.QueryWorkerDbResult.ResultOneofCase.Data, result.ResultCase);
        Assert.Equal(2, result.Data.Rows.Count);
        Assert.True(result.Data.IsAbbreviated);
    }

    static Rpc.QueryWorkerDbRequest RequestWithParams(string query) {
        var request = Request();
        request.Query = query;
        request.Params.Add(new Rpc.DbValue { Int64 = 1 });
        request.Params.Add(new Rpc.DbValue { Int32 = 3 });
        return request;
    }

    [Theory]
    [InlineData("SELECT a FROM t WHERE a > ?1 AND a < ?2")]
    [InlineData("SELECT a FROM t WHERE a < ?2 AND a > ?1")]
    public async Task QueryBindsParametersByNumber(string query) {
        var result = await worker.QueryWorkerDbAsync(RequestWithParams(query));

        Assert.Equal(Rpc.QueryWorkerDbResult.ResultOneofCase.Data, result.ResultCase);
        var row = Assert.Single(result.Data.Rows);
        Assert.Equal(2, row.Values[0].Int64);
    }

    [Fact]
    public async Task QueryWithBareQuestionMarkReturnsError() {
        var result = await worker.QueryWorkerDbAsync(
            RequestWithParams("SELECT a FROM t WHERE a > ? AND a < ?")
        );

        Assert.Equal(Rpc.QueryWorkerDbResult.ResultOneofCase.Error, result.ResultCase);
        Assert.Equal("unknown", result.Error.Code);
    }
}
