using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;

namespace Trell.Observer.Serilog;

public class SerilogObserver : ITrellObserver {
    readonly LogEventLevel level;

    public SerilogObserver(IReadOnlyDictionary<string, string> config) {
        if (config.TryGetValue("level", out var l) && Enum.TryParse(l, out level)) {
            // Successfully set log level from config.
        } else {
            // Otherwise default to Verbose.
            level = LogEventLevel.Verbose;
        }

        Log.Write(level, "Starting Trell.Observer.Serilog.SerilogObserver.");
    }

    void ITrellObserver.OnStartup(TrellProcessInfo info) {
        Log.Write(level, "Trell process {Info} started.", info);
    }

    void ITrellObserver.OnShutdown(TrellProcessInfo info) {
        Log.Write(level, "Trell process {Info} stopped.", info);
    }

    void ITrellObserver.OnMetricsCollected(TrellMetrics metrics) {
        Log.Write(level, "Metrics {Metrics} collected, storage metrics {Storage}.",
            metrics,
            metrics.BytesStored);
    }

    void ITrellObserver.OnJobStarted(object job) {
        Log.Write(level, "Job {Job} started.", job);
    }
    void ITrellObserver.OnJobStopped(object job) {
        Log.Write(level, "Job {Job} stopped.", job);
    }
}
