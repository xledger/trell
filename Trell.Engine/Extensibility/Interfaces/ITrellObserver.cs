namespace Trell.Engine.Extensibility.Interfaces;

public interface ITrellObserver {
    void OnStartup(TrellProcessInfo info) { }
    void OnShutdown(TrellProcessInfo info) { }

    void OnMetricsCollected(TrellMetrics metrics) { }

    void OnJobStarted(object job) { }
    void OnJobStopped(object job) { }
}
