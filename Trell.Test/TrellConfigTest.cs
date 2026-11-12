using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Trell.Test;

public class TrellConfigTest : IDisposable {
    readonly string tempDir = Directory.CreateTempSubdirectory("trell_config_test_").FullName;

    public void Dispose() {
        Directory.Delete(tempDir, true);
        GC.SuppressFinalize(this);
    }

    TrellConfig Load(string toml) {
        var path = Path.Combine(tempDir, $"{Guid.NewGuid()}.toml");
        File.WriteAllText(path, toml);
        return TrellConfig.LoadToml(path);
    }

    const string FULL_CONFIG = """
        socket = "custom-server.sock"
        listen = "127.0.0.1:3000"
        unknown_top_level = 42

        [logger]
        asm = "logger.dll"
        type = "My.Logger"
        config = { level = "info", target = "stdout" }

        [storage]
        path = "store"
        data_path = "custom-data/"
        max_database_page_count = 2048

        [worker]
        id = 3
        socket = "custom-worker.sock"

        [worker.pool]
        single_process = true
        pending = 4
        size = 10
        idle_timeout = "2m"

        [worker.limits]
        max_startup_duration = "250ms"
        max_execution_duration = "15m"
        grace_period = "10s"

        [[plugins]]
        asm = "plugin-a.dll"
        type = "Plugin.A"

        [[plugins]]
        asm = "plugin-b.dll"
        type = "Plugin.B"
        config = { key = "value" }

        [[observers]]
        asm = "observer.dll"
        type = "My.Observer"

        [[Serilog.WriteTo]]
        Name = "Console"
        Args = { "OutputTemplate" = "{Message:lj}" }

        [Serilog.MinimumLevel]
        Default = "Debug"
        Override = { "Microsoft" = "Warning" }
        """;

    [Fact]
    public void ReadsSnakeCaseKeysIntoProperties() {
        var config = Load(FULL_CONFIG);

        Assert.Equal(Path.GetFullPath("custom-server.sock"), config.Socket.ToString());
        Assert.Equal("127.0.0.1:3000", config.Listen);

        Assert.Equal("logger.dll", config.Logger.Asm);
        Assert.Equal("My.Logger", config.Logger.Type);
        Assert.Equal("info", config.Logger.Config["level"]);
        Assert.Equal("stdout", config.Logger.Config["target"]);

        Assert.Equal("store", config.Storage.Path);
        Assert.Equal("custom-data/", config.Storage.DataPath);
        Assert.Equal(2048, config.Storage.MaxDatabasePageCount);

        Assert.Equal(3, config.Worker.Id);
        Assert.Equal(Path.GetFullPath("custom-worker.sock"), config.Worker.Socket.ToString());
        Assert.True(config.Worker.Pool.SingleProcess);
        Assert.Equal(4, config.Worker.Pool.Pending);
        Assert.Equal(10, config.Worker.Pool.Size);
        Assert.Equal(TimeSpan.FromMinutes(2), config.Worker.Pool.IdleTimeout);

        Assert.Equal(TimeSpan.FromMilliseconds(250), config.Worker.Limits.MaxStartupDuration);
        Assert.Equal(TimeSpan.FromMinutes(15), config.Worker.Limits.MaxExecutionDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), config.Worker.Limits.GracePeriod);

        Assert.Equal(["plugin-a.dll", "plugin-b.dll",], config.Plugins.Select(p => p.Asm));
        Assert.Equal(["Plugin.A", "Plugin.B",], config.Plugins.Select(p => p.Type));
        Assert.Empty(config.Plugins[0].Config);
        Assert.Equal("value", config.Plugins[1].Config["key"]);

        var observer = Assert.Single(config.Observers);
        Assert.Equal("observer.dll", observer.Asm);
        Assert.Equal("My.Observer", observer.Type);
    }

    [Fact]
    public void ExposesEveryKeyThroughConfigurationProvider() {
        var config = Load(FULL_CONFIG);
        var provider = (IConfigurationProvider)config;

        Assert.True(config.HasSerilogSettings);
        Assert.True(provider.TryGet("Serilog:WriteTo:0:Name", out var sinkName));
        Assert.Equal("Console", sinkName);
        Assert.True(provider.TryGet("Serilog:WriteTo:0:Args:OutputTemplate", out var template));
        Assert.Equal("{Message:lj}", template);
        Assert.True(provider.TryGet("Serilog:MinimumLevel:Override:Microsoft", out var level));
        Assert.Equal("Warning", level);
        Assert.True(provider.TryGet("storage:max_database_page_count", out var pageCount));
        Assert.Equal("2048", pageCount);
        Assert.True(provider.TryGet("worker:pool:single_process", out var singleProcess));
        Assert.Equal("True", singleProcess);
        Assert.True(provider.TryGet("plugins:1:config:key", out var pluginValue));
        Assert.Equal("value", pluginValue);
    }

    [Fact]
    public void UsesDefaultsForMissingKeys() {
        var config = Load("");

        Assert.Equal(Path.GetFullPath("server.sock"), config.Socket.ToString());
        Assert.Equal(Path.GetFullPath("worker.sock"), config.Worker.Socket.ToString());
        Assert.Equal("", config.Listen);
        Assert.Equal(-1, config.Worker.Id);
        Assert.False(config.Worker.Pool.SingleProcess);
        Assert.Equal(1, config.Worker.Pool.Pending);
        Assert.Equal(Environment.ProcessorCount, config.Worker.Pool.Size);
        Assert.Equal(TimeSpan.FromSeconds(30), config.Worker.Pool.IdleTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, config.Worker.Limits.MaxStartupDuration);
        Assert.Equal(Timeout.InfiniteTimeSpan, config.Worker.Limits.MaxExecutionDuration);
        Assert.Equal(Timeout.InfiniteTimeSpan, config.Worker.Limits.GracePeriod);
        Assert.Equal(".", config.Storage.Path);
        Assert.Equal("data/", config.Storage.DataPath);
        Assert.Equal(1024, config.Storage.MaxDatabasePageCount);
        Assert.Empty(config.Plugins);
        Assert.Empty(config.Observers);
        Assert.Empty(config.Logger.Config);
        Assert.False(config.HasSerilogSettings);
    }

    [Theory]
    [InlineData("\"1500ms\"", 1500)]
    [InlineData("\"30s\"", 30_000)]
    [InlineData("\"2m\"", 120_000)]
    [InlineData("\"00:01:30\"", 90_000)]
    public void ParsesDurations(string value, int expectedMs) {
        var config = Load($"""
            [worker.limits]
            max_execution_duration = {value}
            """);
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMs),
            config.Worker.Limits.MaxExecutionDuration
        );
    }

    [Fact]
    public void ConfigCollectionsPrintTheirContents() {
        var config = Load(FULL_CONFIG);
        Assert.Equal("{ level = info, target = stdout }", config.Logger.Config.ToString());
        Assert.StartsWith("{ PluginConfig { Asm = plugin-a.dll", config.Plugins.ToString());
    }

    [Fact]
    public void RoundTripsThroughToml() {
        var config = Load(FULL_CONFIG);

        Assert.True(config.TryConvertToToml(out var toml));
        var reloaded = Load(toml);

        Assert.Equal(config.Socket, reloaded.Socket);
        Assert.Equal(config.Listen, reloaded.Listen);
        Assert.Equal(config.Storage, reloaded.Storage);
        Assert.Equal(config.Worker.Socket, reloaded.Worker.Socket);
        Assert.Equal(config.Worker.Pool, reloaded.Worker.Pool);
        Assert.Equal(config.Worker.Limits, reloaded.Worker.Limits);
        Assert.Equal(
            config.Plugins.Select(p => (p.Asm, p.Type)),
            reloaded.Plugins.Select(p => (p.Asm, p.Type))
        );
        Assert.Equal("value", reloaded.Plugins[1].Config["key"]);
    }
}
