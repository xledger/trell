using System.Diagnostics;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.ClearScript;
using Trell.Engine;
using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Engine.Utility.IO;
using Trell.Rpc;
using static Trell.Engine.ClearScriptWrappers.EngineWrapper;

namespace Trell.Test.Engine;

public class EngineFixture : IDisposable {
    bool disposed = false;
    public readonly string EngineDir;

    public EngineFixture() {
        this.EngineDir = Directory.CreateTempSubdirectory("trell_engine_test_").FullName;

        WriteWorkerJsFile("worker.js");
        WriteWorkerJsFile("js_file_checking_worker.js", onUpload: """
            const expected = 'testing string';
            const actual = await context.file.text();
            return actual === expected;
            """
        );
        WriteWorkerJsFile("timeout_checking_worker_csharp.js", onCronTrigger: """
            for (let i = 0; i < 10000000; i++) {
                console.log(i);
            }
            """
        );
        WriteWorkerJsFile("timeout_checking_worker_js.js", onCronTrigger: """
            while (true) { }
            """
        );
        WriteWorkerJsFile("top_level_infinite_loop.js", toplevel: """
            while (true) { }
            """
        );
        WriteWorkerJsFile("request_sanity_check.js", onRequest: """
            let expected = 'http://fake.url';
            if (context.request.url !== expected) {
                throw new Error(`Expected: ${expected}, Actual: ${context.request.url}`);
            }

            expected = 'POST';
            if (context.request.method !== expected) {
                throw new Error(`Expected: ${expected}, Actual: ${context.request.method}`);
            }

            expected = 'abcd';
            const td = new TextDecoder();
            let actual = td.decode(context.request.body);
            if (actual !== expected) {
                throw new Error(`Expected: ${expected}, Actual: ${actual}`);
            }

            // JS doesn't do value equality for objects, so this is a workaround
            expected = JSON.stringify({ 'Header0': 'Value0', 'Header1': 'Value1' });
            actual = JSON.stringify(context.request.headers);
            if (actual !== expected) {
                throw new Error(`Expected: ${expected}, Actual: ${actual}`);
            }

            return true;
            """
        );
        WriteWorkerJsFile("cron_sanity_check.js", onCronTrigger: """
            let expected = 'TEST';
            if (context.trigger.cron !== expected) {
                throw new Error(`Expected: ${expected}, Actual: ${context.trigger.cron}`);
            }

            expected = new Date('2011-04-14T02:44:16.0000000Z').toString();
            let actual = context.trigger.timestamp.toString();
            if (actual !== expected) {
                throw new Error(`Expected: ${expected}, Actual: ${actual}`);
            }

            return true;
            """
        );
        WriteWorkerJsFile("upload_sanity_check.js", onUpload: """
            let expected = 'test.txt';
            if (context.file.name !== expected) {
                throw new Error(`Expected: ${expected}, Actual: ${context.file.name}`);
            }

            expected = 'TEST TEXT';
            let actual = await context.file.text();
            if (actual !== expected) {
                throw new Error(`Expected: ${expected}, Actual: ${actual}`);
            }
            
            expected = 'text/plain';
            if (context.file.type !== expected) {
                throw new Error(`Expected: ${expected}, Actual: ${context.file.type}`);
            }

            return true;
            """
        );
    }

    void WriteWorkerJsFile(
        string filename,
        string? toplevel = null,
        string? onCronTrigger = null,
        string? onRequest = null,
        string? onUpload = null
    ) {
        File.WriteAllText(
            Path.GetFullPath(filename, this.EngineDir),
            $$"""
            {{toplevel}}
            async function onCronTrigger(context) {
                {{onCronTrigger}}
            }

            async function onRequest(context) {
                {{onRequest}}
            }

            async function onUpload(context) {
                {{onUpload}}
            }

            export default {
                onCronTrigger,
                onRequest,
                onUpload,
            }

            """
        );
    }

    public void Dispose() {
        if (this.disposed) {
            return;
        }

        this.disposed = true;
        GC.SuppressFinalize(this);

        if (Directory.Exists(this.EngineDir)) {
            Directory.Delete(this.EngineDir, true);
        }
    }

    ~EngineFixture() => Dispose();
}

public class EngineTest(EngineFixture engineFixture) : IClassFixture<EngineFixture> {
    EngineFixture fixture = engineFixture;

    [Fact]
    public async Task TestEngineWrapperOnlyRunsPredeterminedCommands() {
        var eng = MakeNewEngineWrapper();
        var ctx = MakeNewExecutionContext();

        var work = new Work(new(), "{}", this.fixture.EngineDir, "NotAValidFunctionName");
        await Assert.ThrowsAsync<TrellUserException>(async () => await eng.RunWorkAsync(ctx, work));

        string[] validFunctions = ["onCronTrigger", "onRequest", "onUpload"];
        foreach (var function in validFunctions) {
            work = work with { Name = function };
            var x = await eng.RunWorkAsync(ctx, work);
            Assert.NotNull(x);
        }
    }

    [Fact]
    public async Task TestEngineWrapperCorrectlyCreatesAndUploadsFiles() {
        var eng = MakeNewEngineWrapper();
        var ctx = MakeNewExecutionContext();

        bool parsed = TrellPath.TryParseRelative("js_file_checking_worker.js", out var workerPath);
        Assert.True(parsed);

        string original = "testing string";
        var newFile = eng.CreateJsFile("test.txt", "text/plain", Encoding.UTF8.GetBytes(original));
        Assert.NotNull(newFile);

        var work = new Work(new(), "{}", this.fixture.EngineDir, "onUpload") {
            WorkerJs = workerPath!,
            Arg = new Work.ArgType.Raw("file", newFile),
        };
        var actual = await eng.RunWorkAsync(ctx, work);
        Assert.Equal("true", actual);
    }

    [Fact]
    public async Task TestEngineWrapperCanTimeOut() {
        RuntimeLimits limits = new() {
            MaxStartupDuration = TimeSpan.FromSeconds(1),
            MaxExecutionDuration = TimeSpan.FromSeconds(1),
            GracePeriod = TimeSpan.FromMilliseconds(0),
        };
        var eng = MakeNewEngineWrapper(limits);

        var ctx = MakeNewExecutionContext();
        bool parsed = TrellPath.TryParseRelative("timeout_checking_worker_csharp.js", out var workerPath);
        Assert.True(parsed);
        var work = new Work(new(), "{}", this.fixture.EngineDir, "onCronTrigger") {
            WorkerJs = workerPath!,
        };

        await Assert.ThrowsAsync<ScriptInterruptedException>(async () => await eng.RunWorkAsync(ctx, work));

        ctx = MakeNewExecutionContext();
        parsed = TrellPath.TryParseRelative("timeout_checking_worker_js.js", out workerPath);
        Assert.True(parsed);
        work = work with { WorkerJs = workerPath! };

        await Assert.ThrowsAsync<ScriptInterruptedException>(async () => await eng.RunWorkAsync(ctx, work));
    }

    [Fact]
    public async Task TestEngineWrapperCanBeCanceledWithCSharpInterop() {
        const double TIMEOUT_LIMIT = 10;
        RuntimeLimits limits = new() {
            MaxStartupDuration = TimeSpan.FromSeconds(5),
            MaxExecutionDuration = TimeSpan.FromSeconds(TIMEOUT_LIMIT),
            GracePeriod = TimeSpan.FromSeconds(1),
        };
        var eng = MakeNewEngineWrapper(limits);

        var cts = new CancellationTokenSource();
        var ctx = MakeNewExecutionContext(cts);

        // This worker contains calls into code that interfaces with C# (console.log)
        bool parsed = TrellPath.TryParseRelative("timeout_checking_worker_csharp.js", out var workerPath);
        Assert.True(parsed);

        var work = new Work(new(), "{}", this.fixture.EngineDir, "onCronTrigger") {
            WorkerJs = workerPath!,
        };

        var sw = Stopwatch.StartNew();
        var run = eng.RunWorkAsync(ctx, work);
        Thread.Sleep(1000);
        cts.Cancel();
        var e = await Assert.ThrowsAnyAsync<Exception>(async () => await run);
        sw.Stop();
        Assert.True(e is ScriptInterruptedException || e is ScriptEngineException);
        // Sanity check to make sure this succeeded because of manual cancellation, not timeout
        Assert.True(sw.Elapsed.TotalSeconds < TIMEOUT_LIMIT);
    }

    [Fact]
    public async Task TestEngineWrapperCanBeCanceledWithNoCSharpInterop() {
        const double TIMEOUT_LIMIT = 10;
        RuntimeLimits limits = new() {
            MaxStartupDuration = TimeSpan.FromSeconds(5),
            MaxExecutionDuration = TimeSpan.FromSeconds(TIMEOUT_LIMIT),
            GracePeriod = TimeSpan.FromSeconds(1),
        };
        var eng = MakeNewEngineWrapper(limits);

        var cts = new CancellationTokenSource();
        var ctx = MakeNewExecutionContext(cts);

        // This worker contains only JS code
        var parsed = TrellPath.TryParseRelative("timeout_checking_worker_js.js", out var workerPath);
        Assert.True(parsed);

        var work = new Work(new(), "{}", this.fixture.EngineDir, "onCronTrigger") {
            WorkerJs = workerPath!,
        };

        var sw = Stopwatch.StartNew();
        var run = eng.RunWorkAsync(ctx, work);
        Thread.Sleep(1000);
        cts.Cancel();
        await Assert.ThrowsAsync<ScriptInterruptedException>(async () => await run);
        sw.Stop();
        // Sanity check to make sure this succeeded because of manual cancellation, not timeout
        Assert.True(sw.Elapsed.TotalSeconds < TIMEOUT_LIMIT);
    }

    [Fact]
    public async Task TestWorkersCanBeTimedOutAtLoadingStep() {
        RuntimeLimits limits = new() {
            MaxStartupDuration = TimeSpan.FromSeconds(5),
            MaxExecutionDuration = TimeSpan.FromSeconds(5),
            GracePeriod = TimeSpan.FromSeconds(1),
        };
        var eng = MakeNewEngineWrapper(limits);
        var ctx = MakeNewExecutionContext();

        var parsed = TrellPath.TryParseRelative("top_level_infinite_loop.js", out var workerPath);
        Assert.True(parsed);

        var work = new Work(new(), "{}", this.fixture.EngineDir, "onCronTrigger") {
            WorkerJs = workerPath!,
        };

        await Assert.ThrowsAsync<ScriptInterruptedException>(async () => await eng.RunWorkAsync(ctx, work));
    }

    [Fact]
    public async Task TestCronReceivesExpectedContext() {
        var eng = MakeNewEngineWrapper();
        var ctx = MakeNewExecutionContext();

        var parsed = TrellPath.TryParseRelative("cron_sanity_check.js", out var workerPath);
        Assert.True(parsed);

        Function fn = new() {
            OnCronTrigger = new() {
                Cron = "TEST",
                Timestamp = Timestamp.FromDateTime(DateTime.Parse("2011-04-14T02:44:16.0000000Z").ToUniversalTime()),
            }
        };

        var work = new Work(new(), "{}", this.fixture.EngineDir, "onCronTrigger") {
            WorkerJs = workerPath!,
            Arg = fn.ToFunctionArg(eng),
        };

        var result = await eng.RunWorkAsync(ctx, work) as string;
        Assert.Equal("true", result);
    }

    [Fact]
    public async Task TestRequestReceivesExpectedContext() {
        var eng = MakeNewEngineWrapper();
        var ctx = MakeNewExecutionContext();

        var parsed = TrellPath.TryParseRelative("request_sanity_check.js", out var workerPath);
        Assert.True(parsed);

        Function fn = new() {
            OnRequest = new() {
                Url = "http://fake.url",
                Method = "POST",
                Headers = {
                    { "Header0", "Value0" },
                    { "Header1", "Value1" },
                },
                Body = ByteString.CopyFrom(Encoding.UTF8.GetBytes("abcd")),
            }
        };

        var work = new Work(new(), "{}", this.fixture.EngineDir, "onRequest") {
            WorkerJs = workerPath!,
            Arg = fn.ToFunctionArg(eng),
        };

        var result = await eng.RunWorkAsync(ctx, work) as string;
        Assert.Equal("true", result);
    }

    [Fact]
    public async Task TestUploadReceivesExpectedContext() {
        var eng = MakeNewEngineWrapper();
        var ctx = MakeNewExecutionContext();

        var parsed = TrellPath.TryParseRelative("upload_sanity_check.js", out var workerPath);
        Assert.True(parsed);

        Function fn = new() {
            OnUpload = new() {
                Filename = "test.txt",
                Content = ByteString.CopyFrom(Encoding.UTF8.GetBytes("TEST TEXT")),
                Type = "text/plain",
            }
        };

        var work = new Work(new(), "{}", this.fixture.EngineDir, "onUpload") {
            WorkerJs = workerPath!,
            Arg = fn.ToFunctionArg(eng),
        };

        var result = await eng.RunWorkAsync(ctx, work) as string;
        Assert.Equal("true", result);
    }

    static EngineWrapper MakeNewEngineWrapper(RuntimeLimits? limits = null) {
        var rt = new RuntimeWrapper(
            new TrellExtensionContainer(new DummyLogger(), null, [], []),
            new RuntimeWrapper.Config() {
                Limits = limits ?? new(),
            }
        );

        return rt.CreateScriptEngine([]);
    }

    static TrellExecutionContext MakeNewExecutionContext(CancellationTokenSource? cts = null) {
        return new TrellExecutionContext {
            CancellationToken = cts?.Token ?? new CancellationTokenSource().Token,
            Id = "DummyId",
            JsonData = "{}",
            User = new TrellUser { Id = "DummyUser" }
        };
    }
}

class DummyLogger : ITrellLogger {
    void ITrellLogger.Log(TrellExecutionContext ctx, TrellLogLevel logLevel, string msg) {
    }
}
