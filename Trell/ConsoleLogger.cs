using Trell.Engine;
using Trell.Engine.Extensibility.Interfaces;

namespace Trell;

public class ConsoleLogger : ITrellLogger {
    public void Log(TrellExecutionContext ctx, TrellLogLevel logLevel, string msg) {
        var serilogLevel = logLevel switch {
            TrellLogLevel.Warn => Serilog.Events.LogEventLevel.Warning,
            TrellLogLevel.Error => Serilog.Events.LogEventLevel.Error,
            _ => Serilog.Events.LogEventLevel.Information,
        };

        if (string.IsNullOrWhiteSpace(ctx.Id)) {
            Serilog.Log.Write(serilogLevel, "{m}", msg);
        } else {
            Serilog.Log.Write(serilogLevel, "Execution {id} : {m}", ctx.Id, msg);
        }
    }
}
