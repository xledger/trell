namespace Trell.Engine;

public class TrellExecutionContext {
    public required string Id { get; init; }

    public required TrellUser User { get; init; }

    public required string JsonData { get; init; }

    public required CancellationToken CancellationToken { get; init; }

    public TrellExecutionContext WithCancellationToken(CancellationToken tok) => new() {
        Id = this.Id,
        User = this.User,
        JsonData = this.JsonData,
        CancellationToken = tok,
    };
}
