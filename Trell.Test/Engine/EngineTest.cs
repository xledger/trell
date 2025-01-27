using Trell.Engine;
using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using static Trell.Engine.ClearScriptWrappers.EngineWrapper;

namespace Trell.Test.Engine;

public class EngineTest {
    [Fact]
    public async Task Test() {
        var cts = new CancellationTokenSource();
        var ctx = new TrellExecutionContext {
            CancellationToken = cts.Token,
            Id = "DummyId",
            JsonData = "{}",
            User = new TrellUser { Id = "DummyUser" }
        };
        var processInfo = new TrellProcessInfo(Environment.ProcessId, TrellProcessKind.Worker);
        var extContainer = new TrellExtensionContainer(
            logger: new DummyLogger(),
            storage: null,
            plugins: [],
            observers: []
        );
        var cfg = new RuntimeWrapper.Config();
        var rt = new RuntimeWrapper(extContainer, cfg);
        var limits = new RuntimeLimits();

        var eng = rt.CreateScriptEngine([]);
        string tmpDir;
        var rng = new Random();
        while (true) {
            var val = rng.Next();
            var name = $"trell-test-{val}";
            var dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), name));
            if (!Directory.Exists(dir)) {
                Directory.CreateDirectory(dir);
                tmpDir = dir;
                break;
            }
        }

        var work = new Work(limits, "", tmpDir, "test");

        await Assert.ThrowsAsync<Microsoft.ClearScript.ScriptEngineException>(async () => await eng.RunWorkAsync(ctx, work));
    }
}

class DummyLogger : ITrellLogger {
    void ITrellLogger.Log(TrellExecutionContext ctx, TrellLogLevel logLevel, string msg) {
    }
}
