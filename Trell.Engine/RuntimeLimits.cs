namespace Trell.Engine;

public record RuntimeLimits {
    public TimeSpan MaxStartupDuration { get; init; } = Timeout.InfiniteTimeSpan;
    public TimeSpan MaxExecutionDuration { get; init; } = Timeout.InfiniteTimeSpan;
    public TimeSpan GracePeriod { get; init; } = Timeout.InfiniteTimeSpan;

    public TimeSpan Duration() {
        if (MaxStartupDuration == Timeout.InfiniteTimeSpan
            || MaxExecutionDuration == Timeout.InfiniteTimeSpan) {
            return Timeout.InfiniteTimeSpan;
        } else {
            return MaxStartupDuration + MaxExecutionDuration;
        }
    }

    public TimeSpan DurationWithGracePeriod() {
        if (MaxStartupDuration == Timeout.InfiniteTimeSpan
            || MaxExecutionDuration == Timeout.InfiniteTimeSpan
            || GracePeriod == Timeout.InfiniteTimeSpan) {
            return Timeout.InfiniteTimeSpan;
        } else {
            return MaxStartupDuration + MaxExecutionDuration + GracePeriod;
        }
    }

    static TimeSpan SelectShorterPositive(TimeSpan a, TimeSpan b) {
        if ((TimeSpan.Zero <= a && a < b) || b <= TimeSpan.Zero) {
            return a;
        } else {
            return b;
        }
    }

    public RuntimeLimits RestrictBy(RuntimeLimits other) => new() {
        MaxStartupDuration = SelectShorterPositive(MaxStartupDuration, other.MaxStartupDuration),
        MaxExecutionDuration = SelectShorterPositive(MaxExecutionDuration, other.MaxExecutionDuration),
        GracePeriod = SelectShorterPositive(GracePeriod, other.GracePeriod),
    };
}
