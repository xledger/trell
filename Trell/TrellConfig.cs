using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Serilog;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Tomlyn.Model;
using Tomlyn.Syntax;
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

    public static TrellConfig LoadExample() {
        var text = LoadExampleText();
        return ParseToml(text);
    }

    internal static string LoadExampleText() {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Trell.Trell.example.toml")!;
        using var sr = new StreamReader(stream);
        return sr.ReadToEnd();
    }

    static TrellConfig ParseToml(string rawText, string? sourcePath = null) {
        var syntax = Tomlyn.Toml.Parse(rawText, sourcePath);
        var config = Tomlyn.Toml.ToModel<TrellConfig>(syntax, options: TOML_OPTIONS);
        var table = Tomlyn.Toml.ToModel(syntax);
        config.Populate(table);
        config.ConfigPath = sourcePath ?? "";
        return config;
    }

    public bool TryConvertToToml([NotNullWhen(true)] out string? s) {
        return Tomlyn.Toml.TryFromModel(this, out s, out var _, TOML_OPTIONS);
    }

    // Used by IConfigurationProvider implementation.
    readonly Dictionary<string, string?> data = [];

    [System.Runtime.Serialization.IgnoreDataMember]
    public string ConfigPath { get; set; } = string.Empty;

    /// <summary>
    /// Socket path to listen to gRPC requests on. E.g., "server.sock"
    /// </summary>
    public AbsolutePath Socket { get; set; } = "server.sock";

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
        public AbsolutePath Path { get; set; } = "/Temp/TrellUserData";
        public int MaxDatabasePageCount { get; set; } = 1024;
    }

    public record PluginConfig : ExtensionConfig;

    public record ObserverConfig : ExtensionConfig;

    #region IConfigurationProvider
    internal bool HasSerilogSettings => (this as IConfigurationProvider).GetChildKeys([], "Serilog").Any();

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
                    this.data.Add(paths!.Peek(), Convert.ToString(o, CultureInfo.InvariantCulture));
                    break;
            }
        }
    }

    IEnumerable<string> IConfigurationProvider.GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) {
        var results = new List<string>();

        if (parentPath is null) {
            foreach (var k in this.data.Keys) {
                results.Add(Segment(k, 0));
            }
        } else {
            Assert.Equals(ConfigurationPath.KeyDelimiter, ":");

            foreach (var k in this.data.Keys) {
                if (k.Length > parentPath.Length &&
                    k.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase) &&
                    k[parentPath.Length] == ':') {
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

    bool IConfigurationProvider.TryGet(string key, out string? value) => this.data.TryGetValue(key, out value);
    #endregion

    #region TOML Parsing
    [GeneratedRegex("\\A(\\d+)ms\\z")]
    private static partial Regex MillisecondsRegex();

    [GeneratedRegex("\\A(\\d+)s\\z")]
    private static partial Regex SecondsRegex();

    [GeneratedRegex("\\A(\\d+)m\\z")]
    private static partial Regex MinutesRegex();

    static readonly Tomlyn.TomlModelOptions TOML_OPTIONS = new() {
        IgnoreMissingProperties = true,
        CreateInstance = (ty, kind) => {
            if (ty == typeof(Dictionary<string, string>)) {
                return new ConfigDictionary<string, string>();
            } else if (ty == typeof(List<PluginConfig>)) {
                return new ConfigList<PluginConfig>();
            } else if (ty == typeof(List<ObserverConfig>)) {
                return new ConfigList<ObserverConfig>();
            } else {
                return Tomlyn.TomlModelOptions.DefaultCreateInstance(ty, kind);
            }
        },
        ConvertToModel = (obj, ty) => {
            if (obj is string s) {
                if (ty == typeof(TimeSpan)) {
                    static bool TryMatch(Regex r, string s, out int i) {
                        if (r.Matches(s) is MatchCollection matches && matches.Count == 1
                            && matches[0].Groups.Count == 2 && matches[0].Groups[1] is Group g
                            && int.TryParse(g.Value, out i)) {
                            return true;
                        } else {
                            i = 0;
                            return false;
                        }
                    }

                    if (TryMatch(MillisecondsRegex(), s, out var i)) {
                        return TimeSpan.FromMilliseconds(i);
                    } else if (TryMatch(SecondsRegex(), s, out i)) {
                        return TimeSpan.FromSeconds(i);
                    } else if (TryMatch(MinutesRegex(), s, out i)) {
                        return TimeSpan.FromMinutes(i);
                    } else if (TimeSpan.TryParse(s, out var ts)) {
                        return ts;
                    }
                } else if (ty == typeof(AbsolutePath)) {
                    return new AbsolutePath(s);
                }
            }

            return null;
        },
        ConvertToToml = (object obj) => obj switch {
            TimeSpan ts => ts.ToString(),
            AbsolutePath ap => ap.ToString(),
            _ => null
        },
    };
    #endregion

    #region Debugging classes
    class ConfigList<T> : System.Collections.Generic.List<T> {
        public override string? ToString() {
            return "{ " + string.Join(", ", this) + " }";
        }
    }

    class ConfigDictionary<K, V> : System.Collections.Generic.Dictionary<K, V>
    where K : notnull {
        public override string? ToString() {
            return "{ " + string.Join(", ", this.Select(kvp => $"{kvp.Key} = {kvp.Value}")) + " }";
        }
    }
    #endregion
}
