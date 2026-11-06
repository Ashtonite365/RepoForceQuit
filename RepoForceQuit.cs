using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BepInEx;
using UnityEngine;

namespace RepoForceQuit
{
    [BepInPlugin(PluginId, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.ashtonite.repo.forcequit";
        public const string PluginName = "Repo Force Quit";
        public const string PluginVersion = "1.0.1";

        private const int GWLP_WNDPROC = -4;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_QUIT = 0x0012;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_SYSCOMMAND = 0x0112;
        private const int SC_CLOSE = 0xF060;
        private const uint CTRL_C_EVENT = 0;
        private const uint CTRL_BREAK_EVENT = 1;
        private const uint CTRL_CLOSE_EVENT = 2;

        private static bool _killing;
        private IntPtr _hwnd;
        private IntPtr _oldWndProc = IntPtr.Zero;
        private WndProcDelegate _wndProc;
        private IntPtr _consoleHwnd;
        private IntPtr _oldConsoleWndProc = IntPtr.Zero;
        private WndProcDelegate _consoleWndProc;
        private ConsoleCtrlDelegate _consoleCtrl;

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
            Application.wantsToQuit += OnWantsToQuit;
            Application.quitting += KillRepoProcesses;
            _consoleCtrl = OnConsoleCtrl;
            if (!SetConsoleCtrlHandler(_consoleCtrl, true))
                Logger.LogWarning("Could not hook console control events; window hooks are still used.");
            else
                Logger.LogInfo("Hooked console close (BepInEx console X, Ctrl+C).");
            Logger.LogInfo("Repo Force Quit 1.0.1 loaded. Closing the game window X or the BepInEx console force-kills leftover R.E.P.O. processes.");
        }

        private void Update()
        {
            if (_oldWndProc == IntPtr.Zero)
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    _hwnd = hwnd;
                    _wndProc = HookedWndProc;
                    var newPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
                    _oldWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, newPtr);
                    if (_oldWndProc == IntPtr.Zero)
                        Logger.LogWarning("Could not hook the game window; Application quit events are still hooked.");
                    else
                        Logger.LogInfo("Hooked the game window close button.");
                }
            }

            if (_oldConsoleWndProc == IntPtr.Zero)
            {
                var consoleHwnd = GetConsoleWindow();
                if (consoleHwnd != IntPtr.Zero && consoleHwnd != _hwnd)
                {
                    _consoleHwnd = consoleHwnd;
                    _consoleWndProc = HookedConsoleWndProc;
                    var newPtr = Marshal.GetFunctionPointerForDelegate(_consoleWndProc);
                    _oldConsoleWndProc = SetWindowLongPtr(_consoleHwnd, GWLP_WNDPROC, newPtr);
                    if (_oldConsoleWndProc == IntPtr.Zero)
                        Logger.LogWarning("Could not hook the BepInEx console window; console control handler is still hooked.");
                    else
                        Logger.LogInfo("Hooked the BepInEx console close button.");
                }
            }
        }

        private void OnApplicationQuit()
        {
            KillRepoProcesses();
        }

        private void OnDestroy()
        {
            Application.wantsToQuit -= OnWantsToQuit;
            Application.quitting -= KillRepoProcesses;
            if (_consoleCtrl != null)
            {
                SetConsoleCtrlHandler(_consoleCtrl, false);
                _consoleCtrl = null;
            }
            if (_hwnd != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldWndProc);
                _oldWndProc = IntPtr.Zero;
            }
            if (_consoleHwnd != IntPtr.Zero && _oldConsoleWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(_consoleHwnd, GWLP_WNDPROC, _oldConsoleWndProc);
                _oldConsoleWndProc = IntPtr.Zero;
            }
        }

        private IntPtr HookedWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_CLOSE || msg == WM_QUIT ||
                (msg == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_CLOSE))
            {
                KillRepoProcesses();
            }

            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        private IntPtr HookedConsoleWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_CLOSE || msg == WM_DESTROY || msg == WM_QUIT ||
                (msg == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_CLOSE))
            {
                KillRepoProcesses();
            }

            return CallWindowProc(_oldConsoleWndProc, hWnd, msg, wParam, lParam);
        }

        private bool OnConsoleCtrl(uint ctrlType)
        {
            if (ctrlType == CTRL_CLOSE_EVENT || ctrlType == CTRL_C_EVENT || ctrlType == CTRL_BREAK_EVENT)
            {
                KillRepoProcesses();
                return true;
            }

            return false;
        }

        private bool OnWantsToQuit()
        {
            KillRepoProcesses();
            return true;
        }

        internal static void KillRepoProcesses()
        {
            if (_killing)
                return;
            _killing = true;

            try
            {
                foreach (var process in Process.GetProcessesByName("REPO"))
                    TryKill(process);

                foreach (var name in new[] { "UnityCrashHandler64", "UnityCrashHandler32" })
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        if (PathIsUnderRepoInstall(process))
                            TryKill(process);
                    }
                }
            }
            catch
            {
            }
        }

        private static bool PathIsUnderRepoInstall(Process process)
        {
            try
            {
                var file = process.MainModule?.FileName;
                return !string.IsNullOrEmpty(file) &&
                       file.IndexOf("\\steamapps\\common\\REPO\\", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill();
            }
            catch
            {
            }
        }
    }
}
