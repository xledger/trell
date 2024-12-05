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
                Association.ValueOneofCase.Bytes => assoc.Bytes.ToByteArray(), // TODO: Could be Base64 encoded instead.
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
            Function.ValueOneofCase.Scheduled => "scheduled",
            Function.ValueOneofCase.Webhook => "fetch",
            Function.ValueOneofCase.Upload => "upload",
            Function.ValueOneofCase.Dynamic => fn.Dynamic.Name,
            _ => throw new TrellUserException(
                new TrellError(
                    TrellErrorCode.INVALID_REQUEST,
                    $"Workload must specify a function.")),
        };

    public static EngineWrapper.Work.Function.ArgType ToFunctionArg(this Function fn, EngineWrapper engine) =>
        fn?.ValueCase switch {
            Function.ValueOneofCase.Scheduled => new EngineWrapper.Work.Function.ArgType.Raw(new PropertyBag {
                ["cron"] = fn.Scheduled.Cron,
                ["timestamp"] = fn.Scheduled.Timestamp.ToDateTime(),
            }),
            Function.ValueOneofCase.Webhook => new EngineWrapper.Work.Function.ArgType.Raw(new PropertyBag {
                ["url"] = fn.Webhook.Url,
                ["method"] = fn.Webhook.Method,
                ["headers"] = fn.Webhook.Headers.ToPropertyBag(),
                // TODO: This will end up creating an unnecessary allocation and copy.
                ["body"] = fn.Webhook.Body.ToByteArray().SyncRoot,
                // TODO: What we want is to directly convert the Memory
                // TODO: to a Javascript array which requires V8Engine access.
                //["body"] = fn.Webhook.Body.Memory,
            }),
            Function.ValueOneofCase.Upload => new EngineWrapper.Work.Function.ArgType.Raw(
                engine.CreateJsFile(fn.Upload.Filename, fn.Upload.Type, fn.Upload.Content.ToByteArray())
            ),
            Function.ValueOneofCase.Dynamic =>
                fn.Dynamic.Data is null
                  ? EngineWrapper.Work.Function.ArgType.NONE
                  : new EngineWrapper.Work.Function.ArgType.Json(fn.Dynamic.Data.Text),
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

        // TODO: Should this resolve from segments rather than interpolated string?
        var path =
            string.IsNullOrEmpty(request.Workload.CodePath)
              ? $"users/{request.User.UserId}/workers/{request.Workload.WorkerId}/src/"
              : request.Workload.CodePath;
        if (!storage.TryResolvePath(path, out dir, out var error)) {
            throw new TrellUserException(error);
        }

        return new EngineWrapper.Work.Function(limits, env, dir, request.Workload.Function.ToFunctionName()) {
            Arg = request.Workload.Function.ToFunctionArg(engine),
        };
    }

}
