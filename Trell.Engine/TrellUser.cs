namespace Trell.Engine;

public class TrellUser {
    public required string Id { get; init; }
    public Dictionary<string, object> Data { get; } = new();
}
