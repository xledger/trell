using Google.Protobuf.Collections;
using Microsoft.ClearScript;
using Trell.Engine;
using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Engine.Utility.IO;

namespace Trell.Rpc;

static class ToEngine {
    public static Dictionary<string, object> ToDictionary(this AssociationList associations) =>
        associations.InsertInto(new Dictionary<string, object>());
    public static PropertyBag ToPropertyBag(this AssociationList associations) =>
        associations.InsertInto(new PropertyBag());

    public static T InsertInto<T>(this AssociationList associations, T dict) where T : IDictionary<string, object> {
        foreach (var assoc in associations?.Associations ?? []) {
            dict[assoc.Key] = assoc.ValueCase switch {
                Association.ValueOneofCase.String => assoc.String,
                Association.ValueOneofCase.Double => assoc.Double,
                Association.ValueOneofCase.Int32 => assoc.Int32,
                Association.ValueOneofCase.Int64 => assoc.Int64,
                Association.ValueOneofCase.Bool => assoc.Bool,
                Association.ValueOneofCase.Bytes => assoc.Bytes.ToByteArray(),
                Association.ValueOneofCase.List => assoc.List.ToDictionary(),
                _ => null!,
            };
        }
        return dict;
    }

    public static PropertyBag ToPropertyBag<T>(this MapField<string, T> map) {
        var bag = new PropertyBag();
        foreach (var (k, v) in map) {
            bag[k] = v;
        }
        return bag;
    }

    public static TrellUser ToUser(this WorkUser user) {
        var trellUser = new TrellUser {
            Id = user.UserId,
        };
        user.Data?.InsertInto(trellUser.Data);
        return trellUser;
    }

    public static TrellExecutionContext ToExecutionContext(this WorkOrder order, CancellationToken token) {
        var ctx = new TrellExecutionContext {
            Id = order.ExecutionId,
            User = order.User.ToUser(),
            CancellationToken = token,
            JsonData = order.Workload.Data?.Text ?? "{}"
        };
        return ctx;
    }

    public static string ToFunctionName(this Function fn) =>
        fn?.ValueCase switch {
            Function.ValueOneofCase.OnCronTrigger => "onCronTrigger",
            Function.ValueOneofCase.OnRequest => "onRequest",
            Function.ValueOneofCase.OnUpload => "onUpload",
            Function.ValueOneofCase.Dynamic => fn.Dynamic.Name,
            _ => throw new TrellUserException(
                new TrellError(
                    TrellErrorCode.INVALID_REQUEST,
                    $"Workload must specify a function.")),
        };

    public static EngineWrapper.Work.RawArg ToFunctionArg(this Function fn, EngineWrapper engine) =>
        fn?.ValueCase switch {
            Function.ValueOneofCase.OnCronTrigger => new EngineWrapper.Work.RawArg("trigger",
                engine.CreateScriptObject(new Dictionary<string, object> {
                    ["cron"] = fn.OnCronTrigger.Cron,
                    ["timestamp"] = fn.OnCronTrigger.Timestamp.ToDateTime(),
                })),
            Function.ValueOneofCase.OnRequest => new EngineWrapper.Work.RawArg("request",
                engine.CreateScriptObject(new Dictionary<string, object> {
                    ["url"] = fn.OnRequest.Url,
                    ["method"] = fn.OnRequest.Method,
                    ["headers"] = engine.CreateScriptObject(fn.OnRequest.Headers.ToDictionary(x => x.Key, y => (object)y.Value)),
                    // TODO: Replace this with Span<> equivalent once ClearScript is updated to support it:
                    // https://github.com/xledger/trell/issues/28
                    ["body"] = engine.CreateJsBuffer(fn.OnRequest.Body.ToByteArray()),
                })),
            Function.ValueOneofCase.OnUpload => new EngineWrapper.Work.RawArg("file", 
                engine.CreateJsFile(fn.OnUpload.Filename, fn.OnUpload.Type, fn.OnUpload.Content.ToByteArray())
            ),
            Function.ValueOneofCase.Dynamic =>
                new EngineWrapper.Work.RawArg("argv", engine.CreateJsStringArray(fn.Dynamic.Arguments)),
            _ => throw new TrellUserException(
                new TrellError(
                    TrellErrorCode.INVALID_REQUEST,
                    $"Workload must specify a function.")),
        };

    public static RuntimeLimits ToRuntimeLimits(this WorkLimits request) =>
        request == null
        ? new()
        : new() {
            MaxStartupDuration = request.MaxStartup?.ToTimeSpan() ?? TimeSpan.MaxValue,
            MaxExecutionDuration = request.MaxExecution?.ToTimeSpan() ?? TimeSpan.MaxValue,
            GracePeriod = request.MaxGrace?.ToTimeSpan() ?? TimeSpan.MaxValue,
        };

    public static EngineWrapper.Work ToWork(this WorkOrder request, IStorageProvider? storage, EngineWrapper engine) {
        AbsolutePath dir;
        var limits = request.Limits.ToRuntimeLimits();
        var env = request.Workload.Env?.Text ?? "{}";

        ArgumentNullException.ThrowIfNull(storage);

        var path =
            string.IsNullOrEmpty(request.Workload.CodePath)
              ? $"users/{request.User.UserId}/workers/{request.Workload.WorkerId}/src/"
              : request.Workload.CodePath;
        if (!storage.TryResolveTrellPath(path, out dir, out var error)) {
            throw new TrellUserException(error);
        }

        var work = new EngineWrapper.Work(limits, env, dir, request.Workload.Function.ToFunctionName()) {
            Arg = request.Workload.Function.ToFunctionArg(engine),
            JsonUserData = request.Workload.Data?.Text ?? "{}",
        };
        if (string.IsNullOrEmpty(request.Workload.WorkerFilename)) {
            // Use default.
        } else if (TrellPath.TryParseRelative(request.Workload.WorkerFilename, out var workerJs)) {
            work = work with { WorkerJs = workerJs };
        } else {
            throw new TrellUserException(
                new TrellError(
                    TrellErrorCode.INVALID_PATH,
                    $"{request.Workload.WorkerFilename} could not be parsed as a valid path to a file"));
        }
        return work;
    }

}
