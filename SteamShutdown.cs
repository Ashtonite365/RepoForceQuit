using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RepoForceQuit.Tests")]

namespace RepoForceQuit
{
    internal struct SteamShutdownRequest
    {
        public bool ForceStopInProgress;
        public bool OnMainThread;
        public bool AlreadyCalledByThisPlugin;
        public bool ApiStillValid;
    }

    internal struct SteamShutdownDecision
    {
        public bool Call;
        public string Reason;
    }

    internal static class SteamShutdown
    {
        public const string WrapperTypeName = "Steamworks.SteamClient";
        public const string WrapperMethodName = "Shutdown";

        public static bool WritesSteamRegistry
        {
            get { return false; }
        }

        public static bool UsesExitCodeAsSteamSignal
        {
            get { return false; }
        }

        public static bool ShutdownDetachesTrackedProcesses
        {
            get { return false; }
        }

        public static SteamShutdownDecision Decide(SteamShutdownRequest request)
        {
            if (!request.ForceStopInProgress)
            {
                return Skip("Unity teardown still owns SteamManager.OnDestroy");
            }

            if (!request.OnMainThread)
            {
                return Skip("not the main thread");
            }

            if (request.AlreadyCalledByThisPlugin)
            {
                return Skip("this plugin already called SteamClient.Shutdown");
            }

            if (!request.ApiStillValid)
            {
                return Skip("SteamClient.IsValid is false");
            }

            return new SteamShutdownDecision
            {
                Call = true,
                Reason = "force-stop on the main thread, before process exit, because SteamManager.OnDestroy has not released the API"
            };
        }

        private static SteamShutdownDecision Skip(string reason)
        {
            return new SteamShutdownDecision { Call = false, Reason = reason };
        }
    }
}
