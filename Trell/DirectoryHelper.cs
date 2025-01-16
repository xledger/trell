using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Trell;

static class DirectoryHelper {
    internal const string CONFIG_FILE = "Trell.toml";
    internal const string WORKER_FILE = "worker.js";
    internal const string EXAMPLE_CONFIG_RESOURCE_NAME = "Trell.Trell.example.toml";

    internal const string USERS_DIR = "users";
    internal const string WORKERS_DIR = "workers";
    internal const string SRC_DIR = "src";
    internal const string DATA_DIR = "data";

    // TODO: This sets our default user data directory right under the root of the working directory we're currently in,
    // which might not be an ideal place for users.
    // I recommend changing the default to be right under the OS-given "temporary" folder -- this is usually
    // C:\Users\<user>\AppData\Local\Temp\ on Windows and /tmp/ on Linux, which is where most users might expect a "temp"
    // folder to live. (We can get the correct one for our current OS from calling Path.GetTempPath())
    internal static readonly string DEFAULT_USER_DATA_ROOT_DIR = Path.GetFullPath("/Temp/TrellUserData");

    internal static string GetUserDataPath(string userDataRootPath, string userName) {
        return Path.GetFullPath(Path.Combine(userDataRootPath, USERS_DIR, userName, DATA_DIR));
    }
    internal static string GetWorkerDataPath(string userDataRootPath, string userName, string workerName) {
        var local = GetWorkerLocalRoot(userDataRootPath, userName, workerName);
        return Path.Combine(local, DATA_DIR);
    }
    internal static string GetWorkerSrcPath(string userDataRootPath, string userName, string workerName) {
        var local = GetWorkerLocalRoot(userDataRootPath, userName, workerName);
        return Path.Combine(local, SRC_DIR);
    }
    static string GetWorkerLocalRoot(string userDataRootPath, string userName, string workerName) {
        return Path.GetFullPath(Path.Combine(userDataRootPath, USERS_DIR, userName, WORKERS_DIR, workerName));
    }

    internal static void MakeDirectoriesForNewWorker(string userDataRootPath, string userName, string workerName) {
        // Every time we make a new worker, there are 3 folders that need to exist to support it:
        //   <trellUserData>/user/<user>/data
        //   <trellUserData>/user/<user>/workers/<worker>/src (worker.js lives here)
        //   <trellUserData>/user/<user>/workers/<worker>/data
        var userDataDir = GetUserDataPath(userDataRootPath, userName);
        var workerSrcPath = GetWorkerSrcPath(userDataRootPath, userName, workerName);
        var workerDataDir = GetWorkerDataPath(userDataRootPath, userName, workerName);
        Directory.CreateDirectory(userDataDir);
        Directory.CreateDirectory(workerSrcPath);
        Directory.CreateDirectory(workerDataDir);
    }
}
