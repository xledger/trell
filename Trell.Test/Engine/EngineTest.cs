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
    bool disposed = false;
    public readonly string EngineDir;

    public EngineFixture() {
        this.EngineDir = Directory.CreateTempSubdirectory("trell_engine_test_").FullName;

        WriteWorkerJsFile("worker.js");
        WriteWorkerJsFile("js_buffer_checking_worker.js", upload: """
            const expected = [ 0x03, 0x51, 0x44, 0x3A, 0xC5 ];
            for (let i = 0; i < payload.length; i++) {
                if (payload[i] !== expected[i]) {
                    return false;
                }
            }
            return true;
            """
        );
        WriteWorkerJsFile("timeout_checking_worker.js", scheduled: """
            for (let i = 0; i < 10000000; i++) {
                console.log(i);
            }
            """
        );
    }

    void WriteWorkerJsFile(string filename, string? scheduled = null, string? fetch = null, string? upload = null) {
        File.WriteAllText(
            Path.GetFullPath(filename, this.EngineDir),
            $$"""
            async function scheduled(event, env, ctx) {
                {{scheduled}}
            }
            
            function fetch(request, env, ctx) {
                {{fetch}}
            }
            
            function upload(payload, env, ctx) {
                {{upload}}
            }
            
            export default {
                scheduled,
                fetch,
                upload,
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

        string[] validFunctions = ["scheduled", "upload", "fetch"];
        foreach (var function in validFunctions) {
            work = work with { Name = function };
            var x = await eng.RunWorkAsync(ctx, work);
            Assert.NotNull(x);
        }
    }

    [Fact]
    public async Task TestEngineWrapperCorrectlyCreatesJsBuffers() {
        var eng = MakeNewEngineWrapper();
        var ctx = MakeNewExecutionContext();

        bool parsed = TrellPath.TryParseRelative("js_buffer_checking_worker.js", out var workerPath);
        Assert.True(parsed);

        byte[] original = [ 0x03, 0x51, 0x44, 0x3A, 0xC5 ];
        var buffer = eng.CreateJsBuffer(original);
        Assert.NotNull(buffer);

        var work = new Work(new(), "{}", this.fixture.EngineDir, "upload") {
            WorkerJs = workerPath!,
            Arg = new Work.ArgType.Raw(buffer),
        };
        var actual = await eng.RunWorkAsync(ctx, work);
        Assert.Equal("true", actual);
    }

    [Fact]
    public async Task TestEngineWrapperCanTimeOut() {
        RuntimeLimits limits = new() {
            MaxStartupDuration = TimeSpan.FromMilliseconds(1),
            MaxExecutionDuration = TimeSpan.FromMilliseconds(1),
            GracePeriod = TimeSpan.FromMilliseconds(0),
        };

        var eng = MakeNewEngineWrapper(limits);
        var ctx = MakeNewExecutionContext();

        bool parsed = TrellPath.TryParseRelative("timeout_checking_worker.js", out var workerPath);
        Assert.True(parsed);

        var work = new Work(new(), "{}", this.fixture.EngineDir, "scheduled") {
            WorkerJs = workerPath!,
        };

        await Assert.ThrowsAsync<ScriptInterruptedException>(async () => await eng.RunWorkAsync(ctx, work));
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

    static TrellExecutionContext MakeNewExecutionContext() {
        return new TrellExecutionContext {
            CancellationToken = new CancellationTokenSource().Token,
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
