using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Grpc.Core;

namespace Trell.Test.IPC.Server;

/// <summary>
/// Runs the Trell executable as a server with one worker process, and kills that process to
/// check that the server starts another one.
/// </summary>
public partial class WorkerPoolTest : IDisposable {
    static readonly TimeSpan WAIT = TimeSpan.FromSeconds(60);

    readonly string dir;
    readonly Process server;
    readonly ConcurrentQueue<string> output = new();
    readonly Rpc.TrellServer.TrellServerClient client;

    [GeneratedRegex(@"Worker \d+ \(process (\d+)\) started\.")]
    private static partial Regex WorkerStarted();

    public WorkerPoolTest() {
        // Keep the directory short: Unix socket paths are limited to about 100 characters.
        dir = Directory.CreateTempSubdirectory("trell_pool_").FullName;
        var root = dir.Replace('\\', '/');
        File.WriteAllText(
            Path.Join(dir, "Trell.toml"),
            $"""
            socket = "{root}/s.sock"

            [logger]
            type = "Trell.ConsoleLogger"

            [storage]
            path = "{root}"

            [worker.pool]
            size = 1
            pending = 1
            idle_timeout = "0s"

            """
        );
        foreach (var user in new[] { "a", "b", }) {
            var src = Path.Join(dir, "users", user, "workers", "w", "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(
                Path.Join(src, "worker.js"),
                "export default { run: async () => 'done' }\n"
            );
        }

        var exe = Path.Join(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "Trell.exe" : "Trell"
        );
        var start = new ProcessStartInfo {
            FileName = exe,
            ArgumentList = { "serve", "--config", Path.Join(dir, "Trell.toml"), },
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        server = Process.Start(start)!;
        server.OutputDataReceived += (_, e) => {
            if (e.Data is not null) {
                output.Enqueue(e.Data);
            }
        };
        server.ErrorDataReceived += (_, e) => {
            if (e.Data is not null) {
                output.Enqueue(e.Data);
            }
        };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();

        client =
            new(Trell.IPC.Server.Utility.CreateUnixDomainSocketChannel(Path.Join(dir, "s.sock")));
    }

    public void Dispose() {
        // Killing the tree also kills the worker processes.
        server.Kill(entireProcessTree: true);
        server.WaitForExit();
        server.Dispose();
        try {
            Directory.Delete(dir, true);
        } catch (IOException) {
            // A worker process may still hold a file open for a moment after the kill.
        }
    }

    async Task WaitForServerAsync() {
        var deadline = DateTime.UtcNow + WAIT;
        while (true) {
            try {
                await client.EchoAsync(new Rpc.EchoRequest { Messages = { "hi" } });
                return;
            } catch (RpcException) when (DateTime.UtcNow < deadline && !server.HasExited) {
                await Task.Delay(100);
            }
        }
    }

    async Task<Rpc.WorkResult> ExecuteAsync(string user, string executionId) {
        var order = new Rpc.ServerWorkOrder {
            WorkOrder = new() {
                User = new() { UserId = user },
                ExecutionId = executionId,
                Workload = new() {
                    WorkerId = "w",
                    Function = new() { Dynamic = new() { Name = "run" } },
                },
            },
        };
        try {
            return await client.ExecuteAsync(order, deadline: DateTime.UtcNow + WAIT);
        } catch (RpcException e) {
            // The status only says that the handler threw. The server's log says what it threw.
            throw new Exception($"{e.Status}\nServer output:\n{string.Join("\n", output)}", e);
        }
    }

    // The process IDs of the worker processes the server has started, in order.
    List<int> StartedWorkers() => output
        .Select(line => WorkerStarted().Match(line))
        .Where(m => m.Success)
        .Select(m => int.Parse(m.Groups[1].Value))
        .ToList();

    async Task<int> WaitForStartedWorkerAsync(int count) {
        var deadline = DateTime.UtcNow + WAIT;
        while (StartedWorkers().Count < count) {
            Assert.True(DateTime.UtcNow < deadline, string.Join("\n", output));
            await Task.Delay(50);
        }
        return StartedWorkers()[count - 1];
    }

    static async Task KillAsync(int pid) {
        using var worker = Process.GetProcessById(pid);
        worker.Kill();
        await worker.WaitForExitAsync();
        // On Unix the server learns of a child's exit from a signal, which can arrive a moment
        // after the process is gone.
        await Task.Delay(500);
    }

    [Fact]
    public async Task ServerReplacesAWorkerProcessThatWasKilled() {
        await WaitForServerAsync();

        var first = await ExecuteAsync("a", "exec-1");
        Assert.Equal(Rpc.ResultCode.Success, first.Code);
        var firstPid = await WaitForStartedWorkerAsync(1);

        // User a is idle, so user b may take its process. The process is dead, so the server
        // must start a new one rather than hand it over.
        await KillAsync(firstPid);
        var second = await ExecuteAsync("b", "exec-2");
        Assert.Equal(Rpc.ResultCode.Success, second.Code);
        var secondPid = await WaitForStartedWorkerAsync(2);
        Assert.NotEqual(firstPid, secondPid);

        // User b is bound to the new process. Kill that one too.
        await KillAsync(secondPid);
        var third = await ExecuteAsync("b", "exec-3");
        Assert.Equal(Rpc.ResultCode.Success, third.Code);
        var thirdPid = await WaitForStartedWorkerAsync(3);
        Assert.NotEqual(secondPid, thirdPid);
        Assert.Equal(3, StartedWorkers().Count);
    }
}
