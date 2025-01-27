using System.Text;
using Microsoft.ClearScript;
using Trell.Engine;
using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Engine.Utility.IO;
using static Trell.Engine.ClearScriptWrappers.EngineWrapper;

namespace Trell.Test.Engine;

public class EngineFixture : IDisposable {
    bool _disposed = false;
    public readonly string EngineDir;

    public EngineFixture() {
        var rng = new Random();
        while (true) {
            var name = $"trell-test-{rng.Next()}";
            var dir = Path.GetFullPath(name, Path.GetTempPath());
            if (!Directory.Exists(dir)) {
                Directory.CreateDirectory(dir);
                this.EngineDir = dir;
                break;
            }
        }

        File.WriteAllText(Path.GetFullPath("worker.js", this.EngineDir), """
            async function scheduled(event, env, ctx) {
                console.log("scheduled called");
            }
            
            function fetch(request, env, ctx) {
                console.log("fetch called");
            }
            
            function upload(payload, env, ctx) {
                console.log("upload called");
            }
            
            export default {
                scheduled,
                fetch,
                upload,
            }

            """
        );

        File.WriteAllText(Path.GetFullPath("invalid_worker.js", this.EngineDir), """
            async function scheduled(event, env, ctx) {
                var FileReader = require('filereader');
                const f = new File(["test text"], "test.txt", { type: "text/plain" });
                const fr = new FileReader();
                fr.readAsText(f);
                return fr.result;
            }
            
            function fetch(request, env, ctx) {
                const f = new File(["test text"], "test.txt", { type: "text/plain" });
                const fr = new FileReader();
                fr.readAsText(f);
                return fr.result;
            }
            
            function upload(payload, env, ctx) {
                return payload.bytes();
            }
            
            export default {
                scheduled,
                fetch,
                upload,
            }

            """
        );

        File.WriteAllText(Path.GetFullPath("runs_forever_worker.js", this.EngineDir), """
            async function scheduled(event, env, ctx) {
                
            }
            
            function fetch(request, env, ctx) { }
            
            function upload(payload, env, ctx) { }
            
            export default {
                scheduled,
                fetch,
                upload,
            }

            """
        );
    }

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        GC.SuppressFinalize(this);

        if (Directory.Exists(this.EngineDir)) {
            Directory.Delete(this.EngineDir, true);
        }
    }

    ~EngineFixture() => Dispose();
}

public class EngineTest(EngineFixture fixture) : IClassFixture<EngineFixture> {
    EngineFixture _fixture = fixture;

    [Fact]
    public async Task TestEngineWrapperOnlyRunsPredeterminedCommands() {
        var cts = new CancellationTokenSource();
        var ctx = new TrellExecutionContext {
            CancellationToken = cts.Token,
            Id = "DummyId",
            JsonData = "{}",
            User = new TrellUser { Id = "DummyUser" }
        };
        var processInfo = new TrellProcessInfo(Environment.ProcessId, TrellProcessKind.Worker);
        var limits = new RuntimeLimits();

        var eng = MakeNewEngineWrapper();

        var work = new Work(limits, "{}", this._fixture.EngineDir, "NotAValidFunctionName");

        await Assert.ThrowsAsync<Microsoft.ClearScript.ScriptEngineException>(async () => await eng.RunWorkAsync(ctx, work));
    }

    [Fact]
    public async Task TestEngineWrapperCorrectlyCreatesJsFiles() {
        var eng = MakeNewEngineWrapper();
        var limits = new RuntimeLimits();
        bool parsed = TrellPath.TryParseRelative("invalid_worker.js", out var workerPath);
        Assert.True(parsed);
        
        var cts = new CancellationTokenSource();
        var ctx = new TrellExecutionContext {
            CancellationToken = cts.Token,
            Id = "DummyId",
            JsonData = "{}",
            User = new TrellUser { Id = "DummyUser" }
        };

        const string expected = "Expected contents of newly created file";

        var newFile = eng.CreateJsFile("test.txt", "text/plain", Encoding.UTF8.GetBytes(expected));
        Assert.NotNull(newFile);

        // TESTING -- clean up before push

        var work = new Work(limits, "{}", this._fixture.EngineDir, "upload") {
            WorkerJs = workerPath!,
            Arg = new Work.ArgType.Raw(newFile),
            //Arg = new Work.ArgType.Raw(new PropertyBag {
            //    ["cron"] = "* * * * *",
            //    ["timestamp"] = DateTimeOffset.UtcNow,
            //}),
        };
        var actual = await eng.RunWorkAsync(ctx, work);
        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task TestEngineWrapperCorrectlyCreatesJsBuffers() {
        var eng = MakeNewEngineWrapper();
        var limits = new RuntimeLimits();
        bool parsed = TrellPath.TryParseRelative("invalid_worker.js", out var workerPath);
        Assert.True(parsed);

        var cts = new CancellationTokenSource();
        var ctx = new TrellExecutionContext {
            CancellationToken = cts.Token,
            Id = "DummyId",
            JsonData = "{}",
            User = new TrellUser { Id = "DummyUser" }
        };

        byte[] expected = [ 0x03, 0x51, 0x44, 0x3A, 0xC5 ];

        var buffer = eng.CreateJsBuffer(expected);
        Assert.NotNull(buffer);

        var work = new Work(limits, "{}", this._fixture.EngineDir, "upload") {
            WorkerJs = workerPath!,
            Arg = new Work.ArgType.Raw(buffer),
        };
        var result = await eng.RunWorkAsync(ctx, work);
        Assert.NotNull(result);
        var actual = result as byte[];
        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task TestEngineWrapperWorkCanBeCanceled() {
        var eng = MakeNewEngineWrapper();
        var limits = new RuntimeLimits();
        bool parsed = TrellPath.TryParseRelative("invalid_worker.js", out var workerPath);
        Assert.True(parsed);

        var cts = new CancellationTokenSource();
        var ctx = new TrellExecutionContext {
            CancellationToken = cts.Token,
            Id = "DummyId",
            JsonData = "{}",
            User = new TrellUser { Id = "DummyUser" }
        };

        var work = new Work(limits, "{}", this._fixture.EngineDir, "scheduled") {
            WorkerJs = workerPath!,
            Arg = new Work.ArgType.Raw(new PropertyBag {
                ["cron"] = "* * * * *",
                ["timestamp"] = DateTimeOffset.UtcNow,
            }),
        };
        var actual = eng.RunWorkAsync(ctx, work);
        cts.Cancel();
        await actual;
        Assert.NotNull(actual);
        Assert.True(actual.IsCanceled);
    }

    [Fact]
    public async Task TestEngineWrapperWorkCanBeTimedOut() {
        var eng = MakeNewEngineWrapper();
        var limits = new RuntimeLimits() {
            MaxStartupDuration = TimeSpan.FromSeconds(2),
            MaxExecutionDuration = TimeSpan.FromSeconds(2),
            GracePeriod = TimeSpan.FromSeconds(2)
        };
        bool parsed = TrellPath.TryParseRelative("runs_forever_worker.js", out var workerPath);
        Assert.True(parsed);

        var cts = new CancellationTokenSource();
        var ctx = new TrellExecutionContext {
            CancellationToken = cts.Token,
            Id = "DummyId",
            JsonData = "{}",
            User = new TrellUser { Id = "DummyUser" }
        };

        var work = new Work(limits, "{}", this._fixture.EngineDir, "scheduled") {
            WorkerJs = workerPath!,
            Arg = new Work.ArgType.Raw(new PropertyBag {
                ["cron"] = "* * * * *",
                ["timestamp"] = DateTimeOffset.UtcNow,
            }),
        };
        var actual = await eng.RunWorkAsync(ctx, work); ;
        Assert.NotNull(actual);
        // TODO: investigate what happens with timeouts
    }

    static EngineWrapper MakeNewEngineWrapper() {
        var rt = new RuntimeWrapper(
            new TrellExtensionContainer(new DummyLogger(), null, [], []),
            new RuntimeWrapper.Config() {
            }
        );

        return rt.CreateScriptEngine([]);
    }
}

class DummyLogger : ITrellLogger {
    void ITrellLogger.Log(TrellExecutionContext ctx, TrellLogLevel logLevel, string msg) {
    }
}
