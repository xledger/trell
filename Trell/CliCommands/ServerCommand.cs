using Serilog.Events;
using Spectre.Console.Cli;

namespace Trell.CliCommands;

public class ServerCommand : AsyncCommand<ServerCommand.Settings> {
    public sealed class Settings : CommandSettings {
        /// <summary>
        /// Path to Trell TOML config
        /// </summary>
        [CommandOption("--config")]
        public required string Config { get; set; }

        [CommandOption("--log-level")]
        public LogEventLevel? LogLevel { get; set; } = null;
    }

    public async override Task<int> ExecuteAsync(CommandContext context, Settings settings) {
        settings.Validate();
        App.BootstrapLogger(settings.LogLevel);
        var config = TrellConfig.LoadToml(settings.Config);
        var args = context.Remaining.Raw.ToArray();
        using var app = App.InitServer(config, args);
        await app.RunAsync();
        return 0;
    }
}
