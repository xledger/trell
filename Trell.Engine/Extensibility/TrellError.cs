namespace Trell.Engine.Extensibility;

public class TrellError {
    public TrellErrorCode Code { get; }
    public string Message { get; }

    public TrellError(TrellErrorCode code, string message = "") {
        this.Code = code;
        this.Message = message;
    }

    public string CodeAndMessage {
        get {
            var sb = new StringBuilder();
            sb.Append(this.Code);
            if (!string.IsNullOrWhiteSpace(this.Message)) {
                sb.Append(" : ");
                sb.Append(this.Message);
            }
            return sb.ToString();
        }
    }

    public override string ToString() => $"TrellError({this.CodeAndMessage})";
}

public enum TrellErrorCode {
    INVALID_PATH,
    PERMISSION_ERROR,
    INVALID_REQUEST,
    ENTRY_POINT_NOT_DEFINED,
    TIMEOUT,
    UNAUTHORIZED_DATABASE_ACCESS,
}
