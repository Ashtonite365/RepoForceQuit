using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace RepoForceQuit
{
    [BepInPlugin(PluginId, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.ashtonite.repo.forcequit";
        public const string PluginName = "Repo Force Quit";
        public const string PluginVersion = "1.0.3";

        private const int GwlpWndProc = -4;
        private const uint WmClose = 0x0010;
        private const uint WmQuit = 0x0012;
        private const uint WmDestroy = 0x0002;
        private const uint WmSysCommand = 0x0112;
        private const int ScClose = 0xF060;
        private const uint CtrlCEvent = 0;
        private const uint CtrlBreakEvent = 1;
        private const uint CtrlCloseEvent = 2;

        private static Plugin _instance;
        private readonly QuitSession _session = new QuitSession();
        private readonly WindowsProcessInspector _processes = new WindowsProcessInspector();
        private readonly object _pendingGate = new object();

        private ProcessIdentity _self;
        private int _mainThreadId;
        private string _pendingUnityQuit;
        private SessionTick _pendingKill;
        private bool _hasPendingKill;
        private int _killOnce;
        private volatile bool _killFinished;
        private bool _steamShutdownCalled;
        private MethodInfo _steamShutdown;
        private PropertyInfo _steamIsValid;
        private MethodInfo _cancelAuthTicket;
        private FieldInfo _steamManagerInstance;

        private IntPtr _hwnd;
        private IntPtr _oldWndProc = IntPtr.Zero;
        private WndProcDelegate _wndProc;
        private IntPtr _consoleHwnd;
        private IntPtr _oldConsoleWndProc = IntPtr.Zero;
        private WndProcDelegate _consoleWndProc;
        private ConsoleCtrlDelegate _consoleCtrl;
        private bool _consoleHookTried;
        private bool _loggedUnityQuit;
        private bool _loggedQuitting;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate bool ConsoleCtrlDelegate(uint ctrlType);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

        private void Awake()
        {
            _instance = this;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            string captureError;
            _self = _processes.CaptureCurrent(out captureError);
            if (_self == null)
                Logger.LogWarning("Could not capture this process identity. Force stop is disabled. " + captureError);
            else
                Logger.LogInfo("This process pid " + _self.Pid + " created " + _self.CreationFileTime + " image " + _self.ImagePath);

            Application.wantsToQuit += OnWantsToQuit;
            Application.quitting += OnUnityQuitting;
            _consoleCtrl = OnConsoleCtrl;
            if (!SetConsoleCtrlHandler(_consoleCtrl, true))
                Logger.LogWarning("Could not hook console control events; window hooks are still used.");
            else
                Logger.LogInfo("Hooked console close (BepInEx console X, Ctrl+C).");

            CacheSteamShutdown();
            TryPatchInGameQuitButtons();
            var watchdog = new Thread(WatchdogLoop);
            watchdog.IsBackground = true;
            watchdog.Name = "RepoForceQuit.Watchdog";
            watchdog.Start();
            Logger.LogInfo("Repo Force Quit " + PluginVersion + " loaded. In-game Quit runs the game shutdown, then force-stops this process if that hangs. Window X and the BepInEx console still force-stop this instance. Browsers and other processes are left running even if Steam tracked them for this app.");
        }

        private void Update()
        {
            HookWindowsOnce();

            string unityQuit = null;
            var kill = new SessionTick();
            var hasKill = false;
            lock (_pendingGate)
            {
                unityQuit = _pendingUnityQuit;
                _pendingUnityQuit = null;
                hasKill = _hasPendingKill;
                if (hasKill)
                {
                    kill = _pendingKill;
                    _hasPendingKill = false;
                }
            }

            if (hasKill)
            {
                BeginForceStop(kill.Reason, kill.AllowMainThreadSteamShutdown);
                return;
            }

            if (unityQuit != null)
            {
                Logger.LogInfo(unityQuit + " Calling Application.Quit so save and SteamManager.OnDestroy can run.");
                try
                {
                    Application.Quit();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Application.Quit failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private void OnApplicationQuit()
        {
            if (_loggedQuitting)
                return;
            _loggedQuitting = true;
            Logger.LogInfo("OnApplicationQuit. Leaving game save and SteamManager.OnDestroy on their own path.");
        }

        private void OnDestroy()
        {
            Application.wantsToQuit -= OnWantsToQuit;
            Application.quitting -= OnUnityQuitting;
            if (_consoleCtrl != null)
            {
                SetConsoleCtrlHandler(_consoleCtrl, false);
                _consoleCtrl = null;
            }

            if (_hwnd != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(_hwnd, GwlpWndProc, _oldWndProc);
                _oldWndProc = IntPtr.Zero;
            }

            if (_consoleHwnd != IntPtr.Zero && _oldConsoleWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(_consoleHwnd, GwlpWndProc, _oldConsoleWndProc);
                _oldConsoleWndProc = IntPtr.Zero;
            }

            if (_instance == this)
                _instance = null;
        }

        private void HookWindowsOnce()
        {
            if (_oldWndProc == IntPtr.Zero)
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    _hwnd = hwnd;
                    _wndProc = HookedWndProc;
                    var newPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
                    _oldWndProc = SetWindowLongPtr(_hwnd, GwlpWndProc, newPtr);
                    if (_oldWndProc == IntPtr.Zero)
                        Logger.LogWarning("Could not hook the game window; Application quit events are still hooked.");
                    else
                        Logger.LogInfo("Hooked the game window close button.");
                }
            }

            if (!_consoleHookTried)
            {
                var consoleHwnd = GetConsoleWindow();
                if (consoleHwnd != IntPtr.Zero)
                {
                    _consoleHookTried = true;
                    if (consoleHwnd != _hwnd)
                    {
                        _consoleHwnd = consoleHwnd;
                        _consoleWndProc = HookedConsoleWndProc;
                        var newPtr = Marshal.GetFunctionPointerForDelegate(_consoleWndProc);
                        _oldConsoleWndProc = SetWindowLongPtr(_consoleHwnd, GwlpWndProc, newPtr);
                        if (_oldConsoleWndProc == IntPtr.Zero)
                            Logger.LogWarning("Could not hook the BepInEx console window; console control handler is still hooked. Will not retry every frame.");
                        else
                            Logger.LogInfo("Hooked the BepInEx console close button.");
                    }
                }
            }
        }

        private IntPtr HookedWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WmClose || msg == WmQuit ||
                (msg == WmSysCommand && ((int)wParam & 0xFFF0) == ScClose))
            {
                _session.NoteHardClose("game window close", DateTime.UtcNow);
                BeginForceStop("game window close", true);
            }

            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        private IntPtr HookedConsoleWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WmClose || msg == WmDestroy || msg == WmQuit ||
                (msg == WmSysCommand && ((int)wParam & 0xFFF0) == ScClose))
            {
                _session.NoteHardClose("BepInEx console window", DateTime.UtcNow);
                BeginForceStop("BepInEx console window", OnMainThread());
            }

            return CallWindowProc(_oldConsoleWndProc, hWnd, msg, wParam, lParam);
        }

        private bool OnConsoleCtrl(uint ctrlType)
        {
            if (ctrlType == CtrlCloseEvent || ctrlType == CtrlCEvent || ctrlType == CtrlBreakEvent)
            {
                _session.NoteHardClose("console control " + ctrlType, DateTime.UtcNow);
                BeginForceStop("console control " + ctrlType, false);
                return true;
            }

            return false;
        }

        private bool OnWantsToQuit()
        {
            var first = _session.TryNoteUnityWantsToQuit("Application.wantsToQuit", DateTime.UtcNow);
            if (first && !_loggedUnityQuit)
            {
                _loggedUnityQuit = true;
                Logger.LogInfo("Unity wantsToQuit. Not cancelling. Save and SteamManager.OnDestroy stay on the Unity path. Hard stop in " + (int)QuitSession.ShutdownBound.TotalSeconds + "s if this process is still alive.");
            }

            return true;
        }

        private void OnUnityQuitting()
        {
            Logger.LogInfo("Unity quitting event. Forced termination is still bounded. No Steam registry writes.");
        }

        private void WatchdogLoop()
        {
            try
            {
                while (!_killFinished)
                {
                    Thread.Sleep(200);
                    var tick = _session.Tick(DateTime.UtcNow);
                    if (tick.CallApplicationQuit)
                    {
                        lock (_pendingGate)
                        {
                            if (_pendingUnityQuit == null)
                                _pendingUnityQuit = tick.Reason;
                        }
                    }

                    if (!tick.Kill)
                        continue;

                    if (tick.AllowMainThreadSteamShutdown)
                    {
                        lock (_pendingGate)
                        {
                            _pendingKill = tick;
                            _hasPendingKill = true;
                        }

                        var waitUntil = DateTime.UtcNow + QuitSession.HardCloseBackup;
                        while (!_killFinished && DateTime.UtcNow < waitUntil)
                            Thread.Sleep(50);
                    }

                    if (!_killFinished)
                        BeginForceStop(tick.Reason, false);
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Watchdog stopped: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void BeginForceStop(string reason, bool allowSteam)
        {
            if (allowSteam && OnMainThread())
                TryOrderlySteamShutdown();

            if (Interlocked.CompareExchange(ref _killOnce, 1, 0) != 0)
                return;

            _session.SuppressFurtherKills();
            Logger.LogInfo("Force stop (" + reason + "). Exit code 0 is only the process status passed to TerminateProcess. It is not a Steam status code. No Steam registry writes.");
            if (!allowSteam || !OnMainThread())
                Logger.LogInfo("Skipped SteamClient.Shutdown on this path (" + (OnMainThread() ? "immediate or backup stop" : "not the main thread") + ").");

            if (_self == null)
            {
                Logger.LogWarning("Force stop skipped: this process identity was not captured.");
                _killFinished = true;
                return;
            }

            try
            {
                string childError;
                var ignored = _processes.IgnoredDirectChildren(_self.Pid, 32, out childError);
                if (!string.IsNullOrEmpty(childError))
                    Logger.LogWarning("Direct-child scan: " + childError);
                for (var i = 0; i < ignored.Count; i++)
                    Logger.LogInfo(ignored[i]);

                string selectError;
                var decisions = _processes.SelectTargets(_self, out selectError);
                if (!string.IsNullOrEmpty(selectError))
                    Logger.LogWarning("Process scan: " + selectError);

                var logged = 0;
                for (var i = 0; i < decisions.Count; i++)
                {
                    if (logged >= 32)
                    {
                        Logger.LogInfo("Further process decisions omitted.");
                        break;
                    }

                    Logger.LogInfo(decisions[i].Message);
                    logged++;
                }

                for (var i = 0; i < decisions.Count; i++)
                {
                    var decision = decisions[i];
                    if (!decision.Kill || decision.IsSelf)
                        continue;
                    if (!_processes.StillTheSame(decision))
                    {
                        Logger.LogInfo("Skip pid " + decision.Pid + ": identity changed before terminate.");
                        continue;
                    }

                    string killError;
                    if (!_processes.TerminatePid(decision.Pid, out killError))
                        Logger.LogWarning("Could not terminate pid " + decision.Pid + ": " + killError);
                }

                for (var i = 0; i < decisions.Count; i++)
                {
                    if (!decisions[i].Kill || !decisions[i].IsSelf)
                        continue;
                    Logger.LogInfo("Terminating this process pid " + decisions[i].Pid + ".");
                    string killError;
                    if (!_processes.TerminateCurrent(out killError))
                        Logger.LogWarning(killError);
                    break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Force stop failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            _killFinished = true;
        }

        private void TryOrderlySteamShutdown()
        {
            var apiValid = false;
            if (_steamIsValid != null)
            {
                try
                {
                    var value = _steamIsValid.GetValue(null, null);
                    apiValid = value is bool && (bool)value;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("SteamClient.IsValid failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            var decision = SteamShutdown.Decide(new SteamShutdownRequest
            {
                ForceStopInProgress = true,
                OnMainThread = true,
                AlreadyCalledByThisPlugin = _steamShutdownCalled,
                ApiStillValid = apiValid
            });
            if (!decision.Call)
            {
                Logger.LogInfo("Skipped SteamClient.Shutdown: " + decision.Reason + ".");
                return;
            }

            _steamShutdownCalled = true;
            try
            {
                var manager = _steamManagerInstance == null ? null : _steamManagerInstance.GetValue(null);
                if (manager != null && _cancelAuthTicket != null)
                {
                    Logger.LogInfo("Calling SteamManager.CancelSteamAuthTicket before SteamClient.Shutdown.");
                    _cancelAuthTicket.Invoke(manager, null);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("CancelSteamAuthTicket failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                Logger.LogInfo("Calling Steamworks.SteamClient.Shutdown (Facepunch wrapper for SteamAPI_Shutdown).");
                _steamShutdown.Invoke(null, null);
                Logger.LogInfo("SteamClient.Shutdown returned.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("SteamClient.Shutdown failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void CacheSteamShutdown()
        {
            var steamClient = FindType(SteamShutdown.WrapperTypeName);
            if (steamClient == null)
            {
                Logger.LogWarning("Steamworks.SteamClient was not found. The game's SteamManager.OnDestroy remains the SteamAPI_Shutdown path.");
                return;
            }

            _steamIsValid = steamClient.GetProperty("IsValid", BindingFlags.Public | BindingFlags.Static);
            _steamShutdown = steamClient.GetMethod(SteamShutdown.WrapperMethodName, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (_steamShutdown == null || _steamIsValid == null)
            {
                Logger.LogWarning("SteamClient.Shutdown or IsValid was not found. The game's OnDestroy path remains.");
                _steamShutdown = null;
                _steamIsValid = null;
                return;
            }

            var steamManager = FindType("SteamManager");
            if (steamManager != null)
            {
                _steamManagerInstance = steamManager.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _cancelAuthTicket = steamManager.GetMethod("CancelSteamAuthTicket", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            }

            Logger.LogInfo("Found Steamworks.SteamClient.Shutdown. It is used only on a main-thread force stop if IsValid is still true. Happy-path shutdown stays in SteamManager.OnDestroy.");
        }

        private void TryPatchInGameQuitButtons()
        {
            try
            {
                var harmony = new Harmony(PluginId);
                var patched = 0;
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (var i = 0; i < assemblies.Length; i++)
                {
                    if (!string.Equals(assemblies[i].GetName().Name, "Assembly-CSharp", StringComparison.Ordinal))
                        continue;
                    var types = SafeTypes(assemblies[i]);
                    for (var t = 0; t < types.Length; t++)
                    {
                        var type = types[t];
                        if (type == null)
                            continue;
                        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        for (var m = 0; m < methods.Length; m++)
                        {
                            var method = methods[m];
                            if (!QuitButtons.IsInGameQuitMethod(type.Name, method.Name) || method.GetParameters().Length != 0)
                                continue;
                            harmony.Patch(method, postfix: new HarmonyMethod(typeof(Plugin), nameof(InGameQuitPostfix)));
                            patched++;
                            Logger.LogInfo("Patched " + type.FullName + ".ButtonEventQuit. The original method still runs.");
                        }
                    }
                }

                if (patched == 0)
                    Logger.LogWarning("MenuPageMain.ButtonEventQuit and MenuPageEsc.ButtonEventQuit were not patched. In-game Quit is covered only if Unity quit callbacks run.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not patch in-game Quit: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void InGameQuitPostfix(MethodBase __originalMethod)
        {
            var typeName = __originalMethod == null || __originalMethod.DeclaringType == null
                ? ""
                : __originalMethod.DeclaringType.Name;
            var methodName = __originalMethod == null ? "" : __originalMethod.Name;
            if (!QuitButtons.IsInGameQuitMethod(typeName, methodName))
                return;
            if (_instance == null)
                return;
            var source = typeName + "." + methodName;
            if (_instance._session.TryArmInGameQuit(source, DateTime.UtcNow))
            {
                _instance.Logger.LogInfo("In-game Quit observed: " + source + ". Original method continues. If Unity quit has not started in " + (int)QuitSession.InGameGrace.TotalSeconds + "s, Application.Quit is requested.");
            }
        }

        private bool OnMainThread()
        {
            return Thread.CurrentThread.ManagedThreadId == _mainThreadId;
        }

        private static Type FindType(string fullName)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    var type = assemblies[i].GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch (Exception)
                {
                }
            }

            return null;
        }

        private static Type[] SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types ?? new Type[0];
            }
        }
    }
}
