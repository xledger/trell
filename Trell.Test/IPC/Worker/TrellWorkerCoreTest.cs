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
}
