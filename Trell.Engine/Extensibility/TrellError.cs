namespace Trell.Engine.Extensibility;

public class TrellError {
    public TrellErrorCode Code { get; }
    public string Message { get; }

    public TrellError(TrellErrorCode code, string message = "") {
        Code = code;
        Message = message;
    }

    public string CodeAndMessage {
        get {
            var sb = new StringBuilder();
            sb.Append(Code);
            if (!string.IsNullOrWhiteSpace(Message)) {
                sb.Append(" : ");
                sb.Append(Message);
            }
            return sb.ToString();
        }
    }

    public override string ToString() => $"TrellError({CodeAndMessage})";
}

public enum TrellErrorCode {
    INVALID_PATH,
    PERMISSION_ERROR,
    INVALID_REQUEST,
    ENTRY_POINT_NOT_DEFINED,
    TIMEOUT,
    UNAUTHORIZED_DATABASE_ACCESS,
    INVALID_DB_NAME,
}
