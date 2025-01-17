using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Trell;

static class DirectoryHelper {
    static readonly Lazy<string> DEFAULT_USER_DATA_ROOT_DIR_LAZY = new(() => Path.GetFullPath("/Temp/TrellUserData"));
    internal static string DEFAULT_USER_DATA_ROOT_DIR => DEFAULT_USER_DATA_ROOT_DIR_LAZY.Value;

    const string USERS_DIR = "users";
    const string WORKERS_DIR = "workers";
    const string SRC_DIR = "src";
    const string DATA_DIR = "data";

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
