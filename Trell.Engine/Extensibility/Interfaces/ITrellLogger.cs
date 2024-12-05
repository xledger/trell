namespace Trell.Engine.Extensibility.Interfaces;

public enum TrellLogLevel {
    None = 0,
    Info = 1,
    Status = 2,
    Warn = 3,
    Error = 4,
}

public interface ITrellLogger {
    void Log(TrellExecutionContext ctx, TrellLogLevel logLevel, string msg);
}
