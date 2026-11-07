using System;
using System.Collections.Generic;

namespace RepoForceQuit
{
    internal sealed class ProcessIdentity
    {
        public int Pid;
        public long CreationFileTime;
        public string ImagePath;
    }

    internal sealed class ProcessFact
    {
        public int Pid;
        public int ParentPid;
        public long CreationFileTime;
        public long ParentCreationFileTime;
        public string ImagePath;
    }

    internal struct TargetDecision
    {
        public int Pid;
        public bool Kill;
        public bool IsSelf;
        public long CreationFileTime;
        public string ImagePath;
        public string Message;
    }

    internal static class QuitTargeting
    {
        public const string GameFileName = "REPO.exe";
        public const string CrashHandler64 = "UnityCrashHandler64.exe";
        public const string CrashHandler32 = "UnityCrashHandler32.exe";

        private static readonly string[] ProtectedFileNames = new[]
        {
            "opera.exe",
            "opera_crashreporter.exe",
            "steam.exe",
            "steamwebhelper.exe",
            "steamservice.exe",
            "steamerrorreporter.exe",
            "steamerrorreporter64.exe",
            "gameoverlayui.exe",
            "vrserver.exe",
            "vrcompositor.exe",
            "vrmonitor.exe",
            "vrdashboard.exe",
            "vrwebhelper.exe",
            "java.exe",
            "javaw.exe",
            "playit.exe",
            "conhost.exe"
        };

        public static IReadOnlyList<TargetDecision> Select(ProcessIdentity self, IReadOnlyList<ProcessFact> facts)
        {
            var results = new List<TargetDecision>();
            if (self == null || facts == null)
                return results;

            var selfVerified = false;
            for (var i = 0; i < facts.Count; i++)
            {
                if (IsVerifiedSelf(self, facts[i]))
                {
                    selfVerified = true;
                    break;
                }
            }

            var helpers = new List<TargetDecision>();
            TargetDecision selfDecision = default(TargetDecision);
            var haveSelf = false;

            for (var i = 0; i < facts.Count; i++)
            {
                var fact = facts[i];
                if (fact == null)
                    continue;

                if (IsVerifiedSelf(self, fact))
                {
                    if (haveSelf)
                    {
                        results.Add(Skip(fact, "duplicate self pid"));
                        continue;
                    }

                    haveSelf = true;
                    selfDecision = new TargetDecision
                    {
                        Pid = fact.Pid,
                        Kill = true,
                        IsSelf = true,
                        CreationFileTime = fact.CreationFileTime,
                        ImagePath = fact.ImagePath,
                        Message = "kill pid " + fact.Pid + " " + FileName(fact.ImagePath) + " this process"
                    };
                    continue;
                }

                string helperSkip;
                if (IsVerifiedHelper(self, fact, selfVerified, out helperSkip))
                {
                    helpers.Add(new TargetDecision
                    {
                        Pid = fact.Pid,
                        Kill = true,
                        IsSelf = false,
                        CreationFileTime = fact.CreationFileTime,
                        ImagePath = fact.ImagePath,
                        Message = "kill pid " + fact.Pid + " " + FileName(fact.ImagePath) + " child of " + self.Pid
                    });
                }
                else
                {
                    results.Add(Skip(fact, helperSkip));
                }
            }

            helpers.Sort(delegate (TargetDecision a, TargetDecision b) { return a.Pid.CompareTo(b.Pid); });
            results.InsertRange(0, helpers);
            if (haveSelf)
                results.Add(selfDecision);
            return results;
        }

        public static bool IsExplicitlyProtected(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            for (var i = 0; i < ProtectedFileNames.Length; i++)
            {
                if (string.Equals(fileName, ProtectedFileNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public static string IgnoredDirectChildMessage(int pid, string exeFileName)
        {
            var name = string.IsNullOrEmpty(exeFileName) ? "(unnamed)" : exeFileName;
            if (IsExplicitlyProtected(name))
                return "ignore child pid " + pid + " " + name + " (left running; overlay injection and Steam app tracking are not kill signals)";
            return "ignore child pid " + pid + " " + name + " (left running; not a verified REPO helper)";
        }

        public static string FileName(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "";
            var trimmed = path.TrimEnd('\\', '/');
            var index = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            return index < 0 ? trimmed : trimmed.Substring(index + 1);
        }

        public static string DirectoryOf(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "";
            var trimmed = path.TrimEnd('\\', '/');
            var index = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            return index < 0 ? "" : trimmed.Substring(0, index);
        }

        private static bool IsVerifiedSelf(ProcessIdentity self, ProcessFact fact)
        {
            return fact != null &&
                   fact.Pid == self.Pid &&
                   fact.CreationFileTime != 0 &&
                   fact.CreationFileTime == self.CreationFileTime &&
                   SamePath(fact.ImagePath, self.ImagePath) &&
                   NameIs(fact.ImagePath, GameFileName);
        }

        private static bool IsVerifiedHelper(ProcessIdentity self, ProcessFact fact, bool selfVerified, out string skip)
        {
            var name = FileName(fact.ImagePath);
            if (IsExplicitlyProtected(name))
            {
                skip = "left running (" + name + "); overlay injection and Steam app tracking are not kill signals";
                return false;
            }

            if (!NameIs(fact.ImagePath, CrashHandler64) && !NameIs(fact.ImagePath, CrashHandler32))
            {
                skip = "not this process or its UnityCrashHandler (" + (name.Length == 0 ? "no image path" : name) + ")";
                return false;
            }

            if (!selfVerified)
            {
                skip = "helper ignored because this process identity did not match";
                return false;
            }

            if (fact.ParentPid != self.Pid || fact.ParentCreationFileTime == 0 || fact.ParentCreationFileTime != self.CreationFileTime)
            {
                skip = "UnityCrashHandler parent is not this process";
                return false;
            }

            if (fact.CreationFileTime == 0 || fact.CreationFileTime < self.CreationFileTime)
            {
                skip = "UnityCrashHandler creation time is not after this process";
                return false;
            }

            if (!SamePath(DirectoryOf(fact.ImagePath), DirectoryOf(self.ImagePath)))
            {
                skip = "UnityCrashHandler is not in this game folder";
                return false;
            }

            skip = null;
            return true;
        }

        private static TargetDecision Skip(ProcessFact fact, string reason)
        {
            return new TargetDecision
            {
                Pid = fact.Pid,
                Kill = false,
                IsSelf = false,
                CreationFileTime = fact.CreationFileTime,
                ImagePath = fact.ImagePath,
                Message = "skip pid " + fact.Pid + " " + reason
            };
        }

        private static bool NameIs(string path, string fileName)
        {
            return string.Equals(FileName(path), fileName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "";
            return path.Replace('/', '\\').TrimEnd('\\');
        }
    }
}
