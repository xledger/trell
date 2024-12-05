using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;

namespace Trell.IPC.Server;

class WorkerOptions {
    public required TrellConfig Config { get; init; }
    public required WorkerAddress WorkerAddress { get; init; }
    public required Task ConnectionReady { get; init; }
    public required ITrellObserver Observer { get; init; }
    public required TrellExtensionContainer ExtensionContainer { get; init; }
}
