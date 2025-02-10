namespace Trell.Engine.Extensibility;

public enum MetricsTrigger {
    WorkerStart,
    WorkerStop,
    Interval,
    JobStart,
    JobStop,
}

public record Execution(string UserId, IReadOnlyDictionary<string, object> UserData, string ExecutionId);

public record TrellMetrics(int WorkerId, MetricsTrigger Trigger) {
    public required IReadOnlyList<Execution> Executions { get; init; }
    public int ActiveJobs => this.Executions.Count;

    // What is the worker's pid?
    public required int ProcessId { get; init; }

    // How much memory is the worker using?
    public required long PrivateMemorySize64 { get; init; }
    public required long WorkingSet64 { get; init; }
    public required long PeakPagedMemorySize64 { get; init; }
    public required long PeakVirtualMemorySize64 { get; init; }
    public required long PeakWorkingSet64 { get; init; }

    // How much processor is the worker using?
    public required TimeSpan UserProcessorTime { get; init; }
    public required TimeSpan PrivilegedProcessorTime { get; init; }
    public required TimeSpan TotalProcessorTime { get; init; }
    public required double CpuUsage { get; init; }

    // How much network traffic is the worker creating?
    public required long BytesReceived { get; init; }
    public required long BytesSent { get; init; }

    // How much disk is the worker using?
    public required long BytesRead { get; init; }
    public required long BytesWritten { get; init; }
    public required IReadOnlyList<TrellStorageMetric> BytesStored { get; init; }

    // How many files and sockets and external resources is the worker accessing?
    public required int HandleCount { get; init; }
}

public record TrellStorageMetric(string UserId, string? WorkerId, string Name, long Bytes);
