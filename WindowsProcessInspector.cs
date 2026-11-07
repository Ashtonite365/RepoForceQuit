using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace RepoForceQuit
{
    internal sealed class WindowsProcessInspector
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint ProcessTerminate = 0x0001;
        private const uint Th32CsSnapProcess = 0x00000002;

        public ProcessIdentity CaptureCurrent(out string error)
        {
            error = null;
            Process process;
            try
            {
                process = Process.GetCurrentProcess();
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            var fact = Query(process.Id, out error);
            if (fact == null)
                return null;

            if (!string.Equals(QuitTargeting.FileName(fact.ImagePath), QuitTargeting.GameFileName, StringComparison.OrdinalIgnoreCase))
            {
                error = "current image is " + QuitTargeting.FileName(fact.ImagePath) + ", not " + QuitTargeting.GameFileName;
                return null;
            }

            return new ProcessIdentity
            {
                Pid = fact.Pid,
                CreationFileTime = fact.CreationFileTime,
                ImagePath = fact.ImagePath
            };
        }

        public IReadOnlyList<TargetDecision> SelectTargets(ProcessIdentity self, out string error)
        {
            error = null;
            var facts = new List<ProcessFact>();
            if (self == null)
            {
                error = "no captured process identity";
                return new TargetDecision[0];
            }

            string selfError;
            var selfFact = Query(self.Pid, out selfError);
            if (selfFact != null)
                facts.Add(selfFact);
            else
                error = selfError;

            string enumError;
            var helpers = EnumerateCrashHandlers(out enumError);
            if (enumError != null)
                error = enumError;

            for (var i = 0; i < helpers.Count; i++)
            {
                string childError;
                var child = Query(helpers[i].Pid, out childError);
                if (child == null)
                    continue;
                child.ParentPid = helpers[i].ParentPid;
                string parentError;
                child.ParentCreationFileTime = QueryCreation(helpers[i].ParentPid, out parentError);
                facts.Add(child);
            }

            return QuitTargeting.Select(self, facts);
        }

        public IReadOnlyList<string> IgnoredDirectChildren(int selfPid, int cap, out string error)
        {
            error = null;
            var lines = new List<string>();
            if (selfPid <= 0 || cap <= 0)
                return lines;

            var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
            if (snapshot == new IntPtr(-1) || snapshot == IntPtr.Zero)
            {
                error = "process snapshot failed (" + Marshal.GetLastWin32Error() + ")";
                return lines;
            }

            try
            {
                var entry = new ProcessEntry32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                if (!Process32FirstW(snapshot, ref entry))
                {
                    error = "Process32First failed (" + Marshal.GetLastWin32Error() + ")";
                    return lines;
                }

                var omitted = 0;
                do
                {
                    if (unchecked((int)entry.th32ParentProcessID) != selfPid)
                        continue;
                    var name = entry.szExeFile ?? "";
                    if (string.Equals(name, QuitTargeting.GameFileName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, QuitTargeting.CrashHandler64, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, QuitTargeting.CrashHandler32, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (lines.Count >= cap)
                    {
                        omitted++;
                        continue;
                    }

                    lines.Add(QuitTargeting.IgnoredDirectChildMessage(unchecked((int)entry.th32ProcessID), name));
                }
                while (Process32NextW(snapshot, ref entry));

                if (omitted > 0)
                    lines.Add("Further direct children omitted (" + omitted + "). None of them are terminated from this list.");
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return lines;
        }

        public bool StillTheSame(TargetDecision decision)
        {
            if (decision.Pid <= 0 || decision.CreationFileTime == 0 || string.IsNullOrEmpty(decision.ImagePath))
                return false;
            string error;
            var fact = Query(decision.Pid, out error);
            if (fact == null)
                return false;
            return fact.CreationFileTime == decision.CreationFileTime &&
                   string.Equals(Normalize(fact.ImagePath), Normalize(decision.ImagePath), StringComparison.OrdinalIgnoreCase);
        }

        public bool TerminatePid(int pid, out string error)
        {
            error = null;
            var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessTerminate, false, pid);
            if (handle == IntPtr.Zero)
            {
                error = "OpenProcess failed (" + Marshal.GetLastWin32Error() + ")";
                return false;
            }

            try
            {
                if (!TerminateProcess(handle, 0))
                {
                    error = "TerminateProcess failed (" + Marshal.GetLastWin32Error() + ")";
                    return false;
                }

                return true;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        public bool TerminateCurrent(out string error)
        {
            error = null;
            if (!TerminateProcess(GetCurrentProcess(), 0))
            {
                error = "TerminateProcess on this process failed (" + Marshal.GetLastWin32Error() + ")";
                return false;
            }

            return true;
        }

        private static List<HelperRow> EnumerateCrashHandlers(out string error)
        {
            error = null;
            var rows = new List<HelperRow>();
            var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
            if (snapshot == new IntPtr(-1) || snapshot == IntPtr.Zero)
            {
                error = "process snapshot failed (" + Marshal.GetLastWin32Error() + ")";
                return rows;
            }

            try
            {
                var entry = new ProcessEntry32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                if (!Process32FirstW(snapshot, ref entry))
                {
                    error = "Process32First failed (" + Marshal.GetLastWin32Error() + ")";
                    return rows;
                }

                do
                {
                    var name = entry.szExeFile ?? "";
                    if (string.Equals(name, QuitTargeting.CrashHandler64, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, QuitTargeting.CrashHandler32, StringComparison.OrdinalIgnoreCase))
                    {
                        rows.Add(new HelperRow { Pid = unchecked((int)entry.th32ProcessID), ParentPid = unchecked((int)entry.th32ParentProcessID) });
                    }
                }
                while (Process32NextW(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return rows;
        }

        private static long QueryCreation(int pid, out string error)
        {
            var fact = Query(pid, out error);
            return fact == null ? 0 : fact.CreationFileTime;
        }

        private static ProcessFact Query(int pid, out string error)
        {
            error = null;
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
            {
                error = "OpenProcess " + pid + " failed (" + Marshal.GetLastWin32Error() + ")";
                return null;
            }

            try
            {
                long creation, exit, kernel, user;
                if (!GetProcessTimes(handle, out creation, out exit, out kernel, out user) || creation == 0)
                {
                    error = "GetProcessTimes " + pid + " failed (" + Marshal.GetLastWin32Error() + ")";
                    return null;
                }

                var chars = new char[1024];
                var size = chars.Length;
                if (!QueryFullProcessImageNameW(handle, 0, chars, ref size) || size <= 0 || size > chars.Length)
                {
                    error = "QueryFullProcessImageName " + pid + " failed (" + Marshal.GetLastWin32Error() + ")";
                    return null;
                }

                return new ProcessFact
                {
                    Pid = pid,
                    CreationFileTime = creation,
                    ImagePath = new string(chars, 0, size)
                };
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "";
            return path.Replace('/', '\\').TrimEnd('\\');
        }

        private struct HelperRow
        {
            public int Pid;
            public int ParentPid;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr handle, uint exitCode);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr handle, int flags, char[] name, ref int size);
    }
}
