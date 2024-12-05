using Trell.Engine;
using Trell.Engine.ClearScriptWrappers;

namespace Trell.Extensions;

static class ConfigExtensions {
    public static RuntimeWrapper.Config ToRuntimeConfig(this TrellConfig c) => new() {
        Limits = c.ToRuntimeLimits(),
    };

    public static RuntimeLimits ToRuntimeLimits(this TrellConfig c) => new() {
        MaxStartupDuration = c.Worker.Limits.MaxStartupDuration,
        MaxExecutionDuration = c.Worker.Limits.MaxExecutionDuration,
        GracePeriod = c.Worker.Limits.GracePeriod,
    };
}
