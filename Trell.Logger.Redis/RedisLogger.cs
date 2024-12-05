using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trell.Engine;
using Trell.Engine.Extensibility.Interfaces;

namespace Trell.Logger.Redis;

public class RedisLogger : ITrellLogger {
    readonly ConnectionMultiplexer conn;
    readonly IDatabase db;
    readonly string stream;
    readonly RateLimitDef? rateLimit;
    static readonly DateTime RateLimitEpoch = new DateTime(2000, 1, 1, 0, 0, 0, 0, 0, DateTimeKind.Utc);
    static readonly TimeSpan RateLimitWarnInterval = TimeSpan.FromMinutes(5);
    static readonly LuaScript INCR_SCRIPT = LuaScript.Prepare("""
        local current = redis.call('incr', @key)
        redis.call('expire', @key, @expire_sec)
        return current
        """);


    /// <summary>
    /// Creates a new ITrellLogger that will log to a Redis stream.
    /// </summary>
    /// <param name="config">The `config` section of the Trell config.</param>
    /// `config` must contain the following keys:
    ///  1. `configuration` as defined in https://stackexchange.github.io/StackExchange.Redis/Configuration.html
    ///  2. `stream` defining the name of the stream to log to.
    public RedisLogger(IReadOnlyDictionary<string, string> config) {
        if (!config.TryGetValue("configuration", out var configuration)) {
            throw new ArgumentException("`configuration` must be present.");
        }
        if (!config.TryGetValue("stream", out var s) || string.IsNullOrWhiteSpace(s)) {
            throw new ArgumentException("`stream` must be present.");
        }
        if (config.TryGetValue("ratelimit", out var rateLimit)) {
            var m = Regex.Match(rateLimit, @"^(?'num'\d+)\/(?'window_seconds'\d+)$", RegexOptions.ExplicitCapture);
            if (!m.Success) {
                throw new ArgumentException($"if provided, `ratelimit` must match the following pattern:  /^(?'num'\\d+)\\/(?'window_seconds'\\d+)$/");
            }
            if (!uint.TryParse(m.Groups["num"].Value, CultureInfo.InvariantCulture, out var num)
                || num == 0
                || !uint.TryParse(m.Groups["window_seconds"].Value, CultureInfo.InvariantCulture, out var windowSeconds)
                || windowSeconds == 0) {
                throw new ArgumentException($"ratelimit must match the following pattern: positive-integer/positive-integer (num/window-duration-in-seconds).");
            }

            this.rateLimit = new(num, TimeSpan.FromSeconds(windowSeconds));
        }

        this.conn = ConnectionMultiplexer.Connect(configuration);
        this.db = this.conn.GetDatabase();
        this.stream = s;
        Serilog.Log.Information("Starting RedisLogger: {Conn} to {Stream}",
            configuration, this.stream);
    }

    bool CheckRateLimit(TrellExecutionContext ctx, RateLimitDef rateLimit) {
        var userId = ctx.User.Id;
        var now = DateTime.UtcNow;
        var epochSec = (int)(now - RateLimitEpoch).TotalSeconds;
        var win = epochSec / (int)rateLimit.interval.TotalSeconds;
        var k = $"{this.stream}-ratelimit:{userId}:win:{win}";
        var n = (long)this.db.ScriptEvaluate(
            INCR_SCRIPT,
            new { key = (RedisKey)k, expire_sec = (int)Math.Ceiling(rateLimit.interval.TotalSeconds) }
        );

        if (n <= rateLimit.max) {
            return true;
        }

        do {
            var lastLoggedAtKey = $"{this.stream}-ratelimit:{userId}:last-warned";
            string? lastLoggedStr = this.db.StringGet(lastLoggedAtKey);
            if (lastLoggedStr != null
                && DateTime.TryParseExact(lastLoggedStr,
                    "o",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal, out var lastLoggedAt)
                && now < lastLoggedAt + RateLimitWarnInterval) {
                break;
            }

            var didSetKey =
                this.db.StringSet(lastLoggedAtKey, DateTime.UtcNow.ToString("o"),
                expiry: RateLimitWarnInterval,
                when: When.NotExists);

            if (!didSetKey) {
                // Another thread/process got here before we did.
                break;
            }

            var values = new NameValueEntry[] {
                new("level", TrellLogLevel.Warn.ToString()),
                new("execution_id", ctx.Id),
                new("user_id", ctx.User.Id),
                // @FIXME - Include something here so this can get classified as a system log message, not user.
                new("user_data", JsonSerializer.Serialize((IReadOnlyDictionary<string,object>)ctx.User.Data ?? ReadOnlyDictionary<string,object>.Empty)),
                new("message", "Excessive logging, some messages ignored. This message can occur max once per 5 minutes."),
            };
            _ = this.db.StreamAdd(this.stream, values);
        } while (false);

        return false;
    }

    public void Log(TrellExecutionContext ctx, TrellLogLevel logLevel, string msg) {
        if (this.rateLimit is not null) {
            if (!CheckRateLimit(ctx, this.rateLimit)) {
                return;
            }
        }
        var values = new NameValueEntry[] {
            new("level", logLevel.ToString()),
            new("execution_id", ctx.Id),
            new("user_id", ctx.User.Id),
            new("user_data", JsonSerializer.Serialize((IReadOnlyDictionary<string,object>)ctx.User.Data ?? ReadOnlyDictionary<string,object>.Empty)),
            new("message", msg),
        };
        _ = this.db.StreamAdd(this.stream, values, flags: CommandFlags.FireAndForget);
    }

    record RateLimitDef(uint max, TimeSpan interval);
}
