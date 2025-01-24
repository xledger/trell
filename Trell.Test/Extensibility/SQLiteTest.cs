using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Trell.Engine;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Engine.Extensibility.SQLiteApiDotNetObjects;
using Trell.Engine.Utility.Concurrent;
using Trell.Engine.Utility.IO;

namespace Trell.Test.Extensibility;
public class DatabaseFixture : IDisposable {
    public readonly record struct TestDbRow(string a, string b) {
        public override string ToString() => $"({this.a}, {this.b})";
        public static string Concat(TestDbRow[] rows) => rows.Select(x => x.ToString()).Aggregate((x, y) => $"{x}, {y}");
    }

    bool _disposed = false;
    readonly string _tempDatabaseDir;

    public readonly string ValidDbDir;
    public readonly (string, TestDbRow[])[] ValidDbs = [
        ("normal_db_0", [ new("13", "14"), new("0", "16"), new("1700", "-18") ]),
    ];

    public readonly string SharedDbDir;
    public readonly (string, TestDbRow[])[] SharedDbs = [
        ("shared/db_0", [ new("23", "24"), new("330", "346344"), new("-43", "44") ]),
    ];
    public IReadOnlyCollection<string> SharedDbNames => this.SharedDbs.Select(x => x.Item1).ToList();

    public readonly (string, TestDbRow[]) BigIntDb =
        ("big_int_db", [ new("9223372036854775800", "0"), new("-9223372036854775022", "-1"), new("-5", "72057594037927943") ]);
    public readonly (string, TestDbRow[]) UnlistedDb =
        ("shared/unlisted_db", [ new("1", "2"), new("3", "4"), new("5", "6") ]);
    public readonly (string, TestDbRow[]) EditableDb =
        ("editable_db", [ new("44", "12"), new("33", "13") ]);
    public readonly string NonExistentDbName = "does_not_exist";

    public DatabaseFixture() {
        Random rand = new();
        while (true) {
            var path = Path.GetFullPath($"tmp_database_test_{rand.Next()}", Path.GetTempPath());
            if (!Directory.Exists(path)) {
                Directory.CreateDirectory(path);
                this._tempDatabaseDir = path;
                break;
            }
        }

        static void CreateDb(string dbName, string fullDbDir, string columnType, TestDbRow[] dbRows) {
            var dbFilePath = Path.GetFullPath($"{dbName}.db", fullDbDir);
            using var conn = new SqliteConnection($"Data Source={dbFilePath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE main(a {columnType}, b {columnType});
                INSERT INTO main VALUES {TestDbRow.Concat(dbRows)};
                """;
            cmd.ExecuteNonQuery();
            conn.Close();
        }

        // Valid worker databases
        this.ValidDbDir = Path.GetFullPath("valid", this._tempDatabaseDir);
        Directory.CreateDirectory(this.ValidDbDir);

        for (int i = 0; i < this.ValidDbs.Length; i++) {
            var (name, dbRows) = this.ValidDbs[i];
            CreateDb(name, this.ValidDbDir, "INTEGER", dbRows);
        }

        // Creates a database to test whether our JSON solution is good enough to handle long integers
        var (bigIntDbName, bigIntDbRows) = this.BigIntDb;
        CreateDb(bigIntDbName, this.ValidDbDir, "BIGINT", bigIntDbRows);

        // Creates a database to test editing. We can't allow this one to be listed with the other valid worker dbs
        // because these are all persistent for all the tests, so this won't ever revert to its original values
        // after being edited.
        var (editableDbName, editableDbRows) = this.EditableDb;
        CreateDb(editableDbName, this.ValidDbDir, "INTEGER", editableDbRows);

        // Shared databases
        this.SharedDbDir = Path.GetFullPath("shared", this._tempDatabaseDir);
        Directory.CreateDirectory(this.SharedDbDir);

        for (int i = 0; i < this.SharedDbs.Length; i++) {
            var (name, dbRows) = this.SharedDbs[i];
            var trimmedName = name["shared/".Length..];
            CreateDb(trimmedName, this.SharedDbDir, "INTEGER", dbRows);
        }

        // Creates another db in shared directory that won't be included on list of shared dbs
        var (unlistedName, unlistedDbRows) = this.UnlistedDb;
        var trimmedUnlistedName = unlistedName["shared/".Length..];
        CreateDb(trimmedUnlistedName, this.SharedDbDir, "INTEGER", unlistedDbRows);

        // Connections don't actually close and release handles until this gets called
        SqliteConnection.ClearAllPools();
    }

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        GC.SuppressFinalize(this);

        if (Directory.Exists(this._tempDatabaseDir)) {
            Directory.Delete(this._tempDatabaseDir, true);
        }
    }

    ~DatabaseFixture() => Dispose();
}

public class SQLiteTest(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IDisposable {
    const string GET_EVERYTHING_QUERY = """
        SELECT *
        FROM main;
        """;

    readonly record struct TryResolvePathValues(bool ReturnValue, AbsolutePath ResolvedPathValue, TrellError? ErrorValue);
    readonly record struct TryWithRootValues(bool ReturnValue, IStorageProvider? NewStorageValue, TrellError? ErrorValue);
    sealed class FakeProvider(int maxDatabasePageCount, TryResolvePathValues tryResolvePathValues, TryWithRootValues tryWithRootValues) : IStorageProvider {
        public int MaxDatabasePageCount => maxDatabasePageCount;

        public bool TryResolvePath(string path, [NotNullWhen(true)] out AbsolutePath resolvedPath, [NotNullWhen(false)] out TrellError? error) {
            string resolved = tryResolvePathValues.ResolvedPathValue;
            if (!resolved.EndsWith(Path.DirectorySeparatorChar)) {
                resolved += Path.DirectorySeparatorChar;
            }
            resolvedPath = resolved + path;
            error = tryResolvePathValues.ErrorValue;
            return tryResolvePathValues.ReturnValue;
        }

        public bool TryWithRoot(string path, [NotNullWhen(true)] out IStorageProvider? newStorage, [NotNullWhen(false)] out TrellError? error) {
            newStorage = tryWithRootValues.NewStorageValue;
            error = tryWithRootValues.ErrorValue;
            return tryWithRootValues.ReturnValue;
        }
    }
    sealed class FakeAtom : IAtomRead<TrellExecutionContext> {
        static readonly TrellExecutionContext CTX = new() {
            Id = "",
            User = new() { Id = "" },
            JsonData = "",
            CancellationToken = default,
        };
        public TrellExecutionContext? Value => CTX;
    }

    DatabaseFixture _fixture = fixture;
    bool _disposed = false;

    SQLite GetNewSQLiteObj() => new(
        sharedConnector: new SqliteConnector(
            new FakeProvider(
                1,
                new TryResolvePathValues(true, this._fixture.SharedDbDir, null),
                new TryWithRootValues(true, null, null)
            )
        ),
        workerConnector: new SqliteConnector(
            new FakeProvider(
                1,
                new TryResolvePathValues(true, this._fixture.ValidDbDir, null),
                new TryWithRootValues(true, null, null)
            )
        ),
        this._fixture.SharedDbNames,
        new FakeAtom(),
        default!
    );

    [Fact]
    public async Task TestSQLiteThrowsWhenAccessingUnlistedSharedDb() {
        dynamic fakeOptions = new System.Dynamic.ExpandoObject();
        fakeOptions.dbname = this._fixture.UnlistedDb.Item1;

        var sqlObj = GetNewSQLiteObj();
        await Assert.ThrowsAsync<TrellUserException>(async () => await sqlObj.Open(fakeOptions));
    }

    [Fact]
    public async Task TestSQLiteSucceedsWhenAccessingValidSharedDb() {
        var sqlObj = GetNewSQLiteObj();
        foreach (var (dbName, dbRows) in this._fixture.SharedDbs) {
            dynamic fakeOptions = new System.Dynamic.ExpandoObject();
            fakeOptions.dbname = dbName;
            using SQLiteConn conn = await sqlObj.Open(fakeOptions);

            var jsonResult = await conn.QueryOneAsync(GET_EVERYTHING_QUERY);
            Assert.NotNull(jsonResult);
            var expected = dbRows[0];
            var actual = JsonSerializer.Deserialize<DatabaseFixture.TestDbRow>(jsonResult);
            Assert.Equal(expected, actual);

            jsonResult = await conn.QueryAsync(GET_EVERYTHING_QUERY);
            Assert.NotNull(jsonResult);
            var expected_2 = dbRows;
            var actual_2 = JsonSerializer.Deserialize<DatabaseFixture.TestDbRow[]>(jsonResult);
            Assert.Equal(expected_2, actual_2);
        }
    }

    [Fact]
    public async Task TestSQLiteSucceedsWhenAccessingValidWorkerDb() {
        var sqlObj = GetNewSQLiteObj();
        foreach (var (dbName, dbRows) in this._fixture.ValidDbs) {
            dynamic fakeOptions = new System.Dynamic.ExpandoObject();
            fakeOptions.dbname = dbName;
            using SQLiteConn conn = await sqlObj.Open(fakeOptions);

            var jsonResult = await conn.QueryOneAsync(GET_EVERYTHING_QUERY);
            Assert.NotNull(jsonResult);
            var expected = dbRows[0];
            var actual = JsonSerializer.Deserialize<DatabaseFixture.TestDbRow>(jsonResult);
            Assert.Equal(expected, actual);

            jsonResult = await conn.QueryAsync(GET_EVERYTHING_QUERY);
            Assert.NotNull(jsonResult);
            var expected_2 = dbRows;
            var actual_2 = JsonSerializer.Deserialize<DatabaseFixture.TestDbRow[]>(jsonResult);
            Assert.Equal(expected_2, actual_2);
        }
    }

    [Fact]
    public async Task TestSQLiteCanHandleDbWithBigInts() {
        // Covers a regression where SQLiteConn.QueryOneAsync didn't convert numbers to strings like
        // SQLiteConn.QueryAsync did and produced different results as a consequence.
        var sqlObj = GetNewSQLiteObj();
        var (dbName, dbRows) = this._fixture.BigIntDb;

        dynamic fakeOptions = new System.Dynamic.ExpandoObject();
        fakeOptions.dbname = dbName;
        using SQLiteConn conn = await sqlObj.Open(fakeOptions);

        var jsonResult = await conn.QueryOneAsync(GET_EVERYTHING_QUERY);
        Assert.NotNull(jsonResult);
        var expected = dbRows[0];
        var actual = JsonSerializer.Deserialize<DatabaseFixture.TestDbRow>(jsonResult);
        Assert.Equal(expected, actual);

        jsonResult = await conn.QueryAsync(GET_EVERYTHING_QUERY);
        Assert.NotNull(jsonResult);
        var expected_2 = dbRows;
        var actual_2 = JsonSerializer.Deserialize<DatabaseFixture.TestDbRow[]>(jsonResult);
        Assert.Equal(expected_2, actual_2);
    }

    [Fact]
    public async Task TestSQLiteCanEditDb() {
        var sqlObj = GetNewSQLiteObj();
        var (dbName, dbRows) = this._fixture.EditableDb;

        dynamic fakeOptions = new System.Dynamic.ExpandoObject();
        fakeOptions.dbname = dbName;
        using SQLiteConn conn = await sqlObj.Open(fakeOptions);

        await conn.ExecAsync("""
            UPDATE main
            SET b = "5"
            WHERE a = "33";
            """);
        var jsonResult = await conn.QueryAsync(GET_EVERYTHING_QUERY);
        Assert.NotNull(jsonResult);
        var originalRow = dbRows.FirstOrDefault(x => x.a == "33");
        Assert.NotEqual(originalRow, default);
        var expected = originalRow with { b = "5" };
        var parsed = JsonSerializer.Deserialize<DatabaseFixture.TestDbRow[]>(jsonResult);
        Assert.NotNull(parsed);
        var actual = parsed.FirstOrDefault(x => x.a == "33");
        Assert.NotEqual(actual, default);
        Assert.Equal(expected, actual);

        await conn.ExecAsync("""
            INSERT INTO main
            VALUES ("600", "700");
            """);
        jsonResult = await conn.QueryAsync(GET_EVERYTHING_QUERY);
        Assert.NotNull(jsonResult);
        parsed = JsonSerializer.Deserialize<DatabaseFixture.TestDbRow[]>(jsonResult);
        Assert.NotNull(parsed);
        var expectedRowCount = dbRows.Length + 1;
        var actualRowCount = parsed.Length;
        Assert.Equal(expectedRowCount, actualRowCount);
        expected = new("600", "700");
        actual = parsed[^1];
        Assert.Equal(expected, actual);
    }

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        GC.SuppressFinalize(this);

        // This has to be called here because for some reason SqliteConnection.Close()
        // doesn't actually release the connection like you'd expect it to.
        SqliteConnection.ClearAllPools();
    }

    ~SQLiteTest() => Dispose();
}
