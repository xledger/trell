namespace Trell.Engine.Utility.IO;

public readonly struct AbsolutePath(string path) : IEquatable<AbsolutePath> {
    readonly string path = Path.GetFullPath(path);

    public override string ToString() => this.path;

    public override bool Equals(object? obj) => obj is AbsolutePath path && Equals(path);
    public bool Equals(AbsolutePath other) => this.path == other.path;

    public override int GetHashCode() => HashCode.Combine(this.path);

    public static bool operator ==(AbsolutePath left, AbsolutePath right) => left.Equals(right);
    public static bool operator !=(AbsolutePath left, AbsolutePath right) => !(left == right);

    public static implicit operator string(AbsolutePath path) => path.ToString();
    public static implicit operator AbsolutePath(string path) => new(path);
}
