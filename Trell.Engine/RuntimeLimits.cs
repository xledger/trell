namespace Trell.Engine;

public record RuntimeLimits {
    public TimeSpan MaxStartupDuration { get; init; } = Timeout.InfiniteTimeSpan;
    public TimeSpan MaxExecutionDuration { get; init; } = Timeout.InfiniteTimeSpan;
    public TimeSpan GracePeriod { get; init; } = Timeout.InfiniteTimeSpan;

    public TimeSpan Duration() {
        if (this.MaxStartupDuration == Timeout.InfiniteTimeSpan
            || this.MaxExecutionDuration == Timeout.InfiniteTimeSpan) {
            return Timeout.InfiniteTimeSpan;
        } else {
            return this.MaxStartupDuration + this.MaxExecutionDuration;
        }
    }

    public TimeSpan DurationWithGracePeriod() {
        if (this.MaxStartupDuration == Timeout.InfiniteTimeSpan
            || this.MaxExecutionDuration == Timeout.InfiniteTimeSpan
            || this.GracePeriod == Timeout.InfiniteTimeSpan) {
            return Timeout.InfiniteTimeSpan;
        } else {
            return this.MaxStartupDuration + this.MaxExecutionDuration + this.GracePeriod;
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
        MaxStartupDuration = SelectShorterPositive(this.MaxStartupDuration, other.MaxStartupDuration),
        MaxExecutionDuration = SelectShorterPositive(this.MaxExecutionDuration, other.MaxExecutionDuration),
        GracePeriod = SelectShorterPositive(this.GracePeriod, other.GracePeriod),
    };
}
