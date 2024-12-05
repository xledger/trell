using Serilog;
using Spectre.Console.Cli;

namespace Trell;

class Program {
    static async Task<int> Main(string[] args) {
        Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) => {
            e.Cancel = true;
            Environment.Exit(1);
        };

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();

        var app = new CommandApp();
        app.Configure(config => {
            config.SetApplicationName("trell");
            config.AddCommand<CliCommands.ServerCommand>("serve")
                .WithDescription("Start trell as a server, accepting commands via gRPC")
                .WithExample(new[] { "serve", "--config", "Trell.toml" });

            config.AddBranch<CliCommands.RunCommandSettings>("run", run => {
                run.SetDescription("Run scripts, directories, or workers (by id).");
                run.AddExample("run file worker.js --handler cron");
                run.AddExample("run dir my-worker-dir --handler webhook");
                run.AddExample("run worker-id worker-123 --handler upload");

                run.AddCommand<CliCommands.RunWorkerIdCommand>("worker")
                    .WithDescription("Runs the named worker code stored in the server's data");
            });

            // Used by server
            config.AddCommand<CliCommands.WorkerCommand>("worker")
                .IsHidden();
            config.PropagateExceptions();
        });

        try {
            return await app.RunAsync(args);
        } catch (CommandRuntimeException ex) {
            if (ex.Pretty is not null) {
                Spectre.Console.AnsiConsole.Write(ex.Pretty);
            } else {
                Log.Error("{m}", ex.Message);
            }
            return 0;
        } catch (Exception ex) {
            Log.Fatal("Fatal Error: {t} {ex}", ex.GetType().Name, ex);
            return 1;
        } finally {
            Log.CloseAndFlush();
        }
    }
}

