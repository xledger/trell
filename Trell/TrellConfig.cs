using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Serilog;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;
using Trell.Engine.Utility;
using Trell.Engine.Utility.IO;

namespace Trell;

public partial record TrellConfig : IConfigurationProvider {
    public static TrellConfig LoadToml(string sourcePath) {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        if (!File.Exists(sourcePath)) {
            throw new FileNotFoundException("Could not find config.", sourcePath);
        }
        var text = File.ReadAllText(sourcePath);
        return ParseToml(text, sourcePath);
    }

    static TrellConfig ParseToml(string rawText, string? sourcePath = null) {
        var options = TomlOptions(sourcePath);
        var config = TomlSerializer.Deserialize<TrellConfig>(rawText, options)!;
        var table = TomlSerializer.Deserialize<TomlTable>(rawText, options)!;
        config.Populate(table);
        config.ConfigPath = sourcePath ?? "";
        return config;
    }

    public bool TryConvertToToml([NotNullWhen(true)] out string? s) {
        try {
            s = TomlSerializer.Serialize(this, TomlOptions(null));
            return true;
        } catch (TomlException) {
            s = null;
            return false;
        }
    }

    // Used by IConfigurationProvider implementation.
    readonly Dictionary<string, string?> data = [];

    [JsonIgnore]
    public string ConfigPath { get; set; } = string.Empty;

    /// <summary>
    /// Socket path to listen to gRPC requests on. E.g., "server.sock"
    /// </summary>
    public AbsolutePath Socket { get; set; } = "server.sock";

    /// <summary>
    /// TCP address on which the server also accepts gRPC requests, as "host:port". E.g.,
    /// "127.0.0.1:3000". Empty, the default, means the server listens on its socket only.
    /// The gRPC API has no authentication: anyone who can reach this address can run
    /// workers, query their databases, and delete them.
    /// </summary>
    public string Listen { get; set; } = "";

    public WorkerConfig Worker { get; set; } = new();

    public LoggerConfig Logger { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public List<PluginConfig> Plugins { get; set; } = new ConfigList<PluginConfig>();
    public List<ObserverConfig> Observers { get; set; } = new ConfigList<ObserverConfig>();

    public record WorkerConfig {
        public int Id { get; set; } = -1;
        public AbsolutePath Socket { get; set; } = "worker.sock";
        public PoolConfig Pool { get; set; } = new();
        public LimitsConfig Limits { get; set; } = new();

        public record PoolConfig {
            public bool SingleProcess { get; set; } = false;
            public int Pending { get; set; } = 1;
            public int Size { get; set; } = Environment.ProcessorCount;
        }

        public record LimitsConfig {
            public TimeSpan MaxStartupDuration { get; set; } = Timeout.InfiniteTimeSpan;
            public TimeSpan MaxExecutionDuration { get; set; } = Timeout.InfiniteTimeSpan;
            public TimeSpan GracePeriod { get; init; } = Timeout.InfiniteTimeSpan;
        }
    }

    public record ExtensionConfig {
        public string Asm { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, string> Config { get; set; } = new ConfigDictionary<string, string>();
    }

    public record LoggerConfig : ExtensionConfig;

    public record StorageConfig {
        public string Path { get; set; } = ".";
        public string DataPath { get; set; } = "data/";
        public int MaxDatabasePageCount { get; set; } = 1024;
    }

    public record PluginConfig : ExtensionConfig;

    public record ObserverConfig : ExtensionConfig;

    #region IConfigurationProvider
    internal bool HasSerilogSettings =>
        (this as IConfigurationProvider).GetChildKeys([], "Serilog").Any();

    void Populate(TomlTable root) {
        var paths = new Stack<string>();
        void Push(string path) {
            if (paths!.Count > 0) {
                path = $"{paths!.Peek()}{ConfigurationPath.KeyDelimiter}{path}";
            }
            paths!.Push(path);
        }
        void Pop() => paths!.Pop();

        AddTable(root);

        void AddTable(TomlTable t) {
            foreach (var (k, v) in t) {
                Push(k);
                AddObject(v);
                Pop();
            }
        }

        void AddArray(TomlArray a) {
            for (int i = 0, n = a.Count; i < n; ++i) {
                Push(i.ToString());
                AddObject(a[i]!);
                Pop();
            }
        }

        void AddTableArray(TomlTableArray ta) {
            for (int i = 0, n = ta.Count; i < n; ++i) {
                Push(i.ToString());
                AddTable(ta[i]);
                Pop();
            }
        }

        void AddObject(object o) {
            switch (o) {
                case TomlTable t:
                    AddTable(t);
                    break;
                case TomlArray a:
                    AddArray(a);
                    break;
                case TomlTableArray ta:
                    AddTableArray(ta);
                    break;
                default:
                    data.Add(paths!.Peek(), Convert.ToString(o, CultureInfo.InvariantCulture));
                    break;
            }
        }
    }

    IEnumerable<string> IConfigurationProvider.GetChildKeys(
        IEnumerable<string> earlierKeys,
        string? parentPath
    ) {
        var results = new List<string>();

        if (parentPath is null) {
            foreach (var k in data.Keys) {
                results.Add(Segment(k, 0));
            }
        } else {
            Assert.Equals(ConfigurationPath.KeyDelimiter, ":");

            foreach (var k in data.Keys) {
                if (k.Length > parentPath.Length
                    && k.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase)
                    && k[parentPath.Length] == ':'
                ) {
                    results.Add(Segment(k, parentPath.Length + 1));
                }
            }
        }

        results.AddRange(earlierKeys);

        results.Sort(ConfigurationKeyComparer.Instance.Compare);

        return results;

        static string Segment(string key, int prefixLength) {
            Assert.Equals(ConfigurationPath.KeyDelimiter, ":");
            var indexOf = key.IndexOf(':', prefixLength);
            return indexOf < 0 ? key[prefixLength..] : key[prefixLength..indexOf];
        }
    }

    IChangeToken IConfigurationProvider.GetReloadToken() => null!;

    void IConfigurationProvider.Load() { }

    void IConfigurationProvider.Set(string key, string? value) {
        Log.Information("IConfigurationProvider.Set {key} to {value}", key, value);
        throw new NotImplementedException();
    }

    bool IConfigurationProvider.TryGet(string key, out string? value) =>
        data.TryGetValue(key, out value);
    #endregion

    #region TOML Parsing
    [GeneratedRegex("\\A(\\d+)ms\\z")]
    private static partial Regex MillisecondsRegex();

    [GeneratedRegex("\\A(\\d+)s\\z")]
    private static partial Regex SecondsRegex();

    [GeneratedRegex("\\A(\\d+)m\\z")]
    private static partial Regex MinutesRegex();

    // TOML keys are snake_case versions of the property names, e.g. data_path for DataPath.
    // Populate fills the instances the property initializers create, so collections keep
    // the debugging subclasses below.
    static TomlSerializerOptions TomlOptions(string? sourcePath) => new() {
        SourceName = sourcePath,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        Converters = [new TimeSpanConverter(), new AbsolutePathConverter(),],
    };

    // Accepts "250ms", "30s", "15m", or anything TimeSpan.Parse accepts, such as "00:01:30".
    sealed class TimeSpanConverter : TomlConverter<TimeSpan> {
        public override TimeSpan Read(TomlReader reader) {
            if (reader.TokenType == TomlTokenType.String) {
                var s = reader.GetString();
                if (TryMatch(MillisecondsRegex(), s, out var i)) {
                    return TimeSpan.FromMilliseconds(i);
                } else if (TryMatch(SecondsRegex(), s, out i)) {
                    return TimeSpan.FromSeconds(i);
                } else if (TryMatch(MinutesRegex(), s, out i)) {
                    return TimeSpan.FromMinutes(i);
                } else if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts)) {
                    return ts;
                }
            }
            throw reader.CreateException(
                "Expected a duration such as \"250ms\", \"30s\", \"15m\", or \"00:01:30\"."
            );

            static bool TryMatch(Regex r, string s, out int i) {
                if (r.Matches(s) is MatchCollection matches
                    && matches.Count == 1
                    && matches[0].Groups.Count == 2
                    && matches[0].Groups[1] is Group g
                    && int.TryParse(g.Value, out i)
                ) {
                    return true;
                } else {
                    i = 0;
                    return false;
                }
            }
        }

        public override void Write(TomlWriter writer, TimeSpan value) =>
            writer.WriteStringValue(value.ToString());
    }

    sealed class AbsolutePathConverter : TomlConverter<AbsolutePath> {
        public override AbsolutePath Read(TomlReader reader) => reader.TokenType
            == TomlTokenType.String
                ? new AbsolutePath(reader.GetString())
                : throw reader.CreateException("Expected a path string.");

        public override void Write(TomlWriter writer, AbsolutePath value) =>
            writer.WriteStringValue(value.ToString());
    }
    #endregion

    #region Debugging classes
    class ConfigList<T> : System.Collections.Generic.List<T> {
        public override string? ToString() {
            return "{ " + string.Join(", ", this) + " }";
        }
    }

    class ConfigDictionary<K, V>
        : System.Collections.Generic.Dictionary<K, V>
        where K : notnull
    {
        public override string? ToString() {
            return "{ " + string.Join(", ", this.Select(kvp => $"{kvp.Key} = {kvp.Value}")) + " }";
        }
    }
    #endregion
}
