namespace Trell.Engine.Extensibility;

public class TrellUserException : TrellException {
    public TrellError Error { get; }
    public bool IsUserException => true;

    public TrellUserException(TrellError error)
        : base(error.CodeAndMessage) {
        this.Error = error;
    }
}
