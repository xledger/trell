namespace Trell.IPC.Server;

record WorkerAddress(int WorkerId, string SocketPath) {
    static int nextChildId = 0;

    internal static WorkerAddress GetNext(string parentSocketPath) {
        var directory = Path.GetDirectoryName(parentSocketPath);
        if (directory is null) {
            throw new ArgumentOutOfRangeException(nameof(parentSocketPath), "socket must be in a folder");
        }

        var extension = Path.GetExtension(parentSocketPath);
        var baseName = Path.GetFileNameWithoutExtension(parentSocketPath);

        while (true) {
            var childId = Interlocked.Increment(ref nextChildId);
            var randomSuffix = $"-child-{childId}";
            var path = Path.Combine(directory, baseName + randomSuffix + extension);
            if (!File.Exists(path)) {
                return new WorkerAddress(childId, path);
            }
        }
    }
}
