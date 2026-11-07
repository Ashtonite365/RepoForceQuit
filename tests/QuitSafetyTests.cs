using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RepoForceQuit;
using Xunit;

namespace RepoForceQuit.Tests
{
    public class QuitSafetyTests
    {
        private const long SelfCreated = 1000;
        private const string GamePath = @"C:\Program Files (x86)\Steam\steamapps\common\REPO\REPO.exe";
        private const string HandlerPath = @"C:\Program Files (x86)\Steam\steamapps\common\REPO\UnityCrashHandler64.exe";

        private static ProcessIdentity Self()
        {
            return new ProcessIdentity
            {
                Pid = 30496,
                CreationFileTime = SelfCreated,
                ImagePath = GamePath
            };
        }

        private static ProcessFact SelfFact()
        {
            return new ProcessFact
            {
                Pid = 30496,
                CreationFileTime = SelfCreated,
                ImagePath = GamePath
            };
        }

        private static IEnumerable<TargetDecision> Kills(IReadOnlyList<TargetDecision> decisions)
        {
            return decisions.Where(decision => decision.Kill);
        }

        [Fact]
        public void SelectsThisProcessWhenPidCreationAndImageMatch()
        {
            var kills = Kills(QuitTargeting.Select(Self(), new[] { SelfFact() })).ToArray();
            Assert.Single(kills);
            Assert.True(kills[0].IsSelf);
            Assert.Equal(30496, kills[0].Pid);
        }

        [Fact]
        public void DoesNotSelectAnotherRepoProcessWithTheSameImage()
        {
            var other = SelfFact();
            other.Pid = 111;
            other.CreationFileTime = 50;
            var kills = Kills(QuitTargeting.Select(Self(), new[] { SelfFact(), other })).ToArray();
            Assert.Single(kills);
            Assert.Equal(30496, kills[0].Pid);
        }

        [Fact]
        public void DoesNotSelectSelfAfterPidReuse()
        {
            var reused = SelfFact();
            reused.CreationFileTime = SelfCreated + 10;
            var kills = Kills(QuitTargeting.Select(Self(), new[] { reused }));
            Assert.Empty(kills);
        }

        [Fact]
        public void DoesNotSelectSelfWhenImagePathDiffers()
        {
            var reused = SelfFact();
            reused.ImagePath = @"D:\Other\REPO.exe";
            Assert.Empty(Kills(QuitTargeting.Select(Self(), new[] { reused })));
        }

        [Fact]
        public void DoesNotSelectOperaCrashReporterOrLooseRepoNames()
        {
            var facts = new[]
            {
                SelfFact(),
                new ProcessFact
                {
                    Pid = 5,
                    ParentPid = 30496,
                    ParentCreationFileTime = SelfCreated,
                    CreationFileTime = SelfCreated + 5,
                    ImagePath = @"C:\Program Files\Opera\opera_crashreporter.exe"
                },
                new ProcessFact
                {
                    Pid = 6,
                    CreationFileTime = SelfCreated,
                    ImagePath = @"C:\Games\REPOSITORY.exe"
                },
                new ProcessFact
                {
                    Pid = 7,
                    ParentPid = 30496,
                    ParentCreationFileTime = SelfCreated,
                    CreationFileTime = SelfCreated + 5,
                    ImagePath = @"C:\Program Files (x86)\Steam\steamapps\common\REPO\not-a-handler.exe"
                }
            };

            var kills = Kills(QuitTargeting.Select(Self(), facts)).ToArray();
            Assert.Single(kills);
            Assert.True(kills[0].IsSelf);
            Assert.Contains(QuitTargeting.Select(Self(), facts), decision => decision.Message.Contains("opera_crashreporter.exe"));
        }

        [Fact]
        public void SelectsOnlyThisProcessCrashHandlerChild()
        {
            var handler = new ProcessFact
            {
                Pid = 47536,
                ParentPid = 30496,
                ParentCreationFileTime = SelfCreated,
                CreationFileTime = SelfCreated + 20,
                ImagePath = HandlerPath
            };
            var otherInstanceHandler = new ProcessFact
            {
                Pid = 9,
                ParentPid = 8,
                ParentCreationFileTime = 1,
                CreationFileTime = 2,
                ImagePath = HandlerPath
            };
            var elsewhere = new ProcessFact
            {
                Pid = 10,
                ParentPid = 30496,
                ParentCreationFileTime = SelfCreated,
                CreationFileTime = SelfCreated + 20,
                ImagePath = @"D:\Tools\UnityCrashHandler64.exe"
            };
            var older = new ProcessFact
            {
                Pid = 11,
                ParentPid = 30496,
                ParentCreationFileTime = SelfCreated,
                CreationFileTime = SelfCreated - 1,
                ImagePath = HandlerPath
            };
            var reusedParent = new ProcessFact
            {
                Pid = 12,
                ParentPid = 30496,
                ParentCreationFileTime = SelfCreated + 99,
                CreationFileTime = SelfCreated + 100,
                ImagePath = HandlerPath
            };

            var kills = Kills(QuitTargeting.Select(Self(), new[]
            {
                SelfFact(), handler, otherInstanceHandler, elsewhere, older, reusedParent
            })).ToArray();

            Assert.Equal(2, kills.Length);
            Assert.Equal(47536, kills[0].Pid);
            Assert.False(kills[0].IsSelf);
            Assert.True(kills[1].IsSelf);
        }

        [Fact]
        public void DoesNotKillOperaEvenWhenItIsOurChildInTheGameFolder()
        {
            var opera = new ProcessFact
            {
                Pid = 9560,
                ParentPid = 30496,
                ParentCreationFileTime = SelfCreated,
                CreationFileTime = SelfCreated + 30,
                ImagePath = @"C:\Program Files (x86)\Steam\steamapps\common\REPO\opera.exe"
            };
            var reporter = new ProcessFact
            {
                Pid = 47368,
                ParentPid = 9560,
                ParentCreationFileTime = SelfCreated + 30,
                CreationFileTime = SelfCreated + 31,
                ImagePath = @"C:\Users\ashto\AppData\Local\Programs\Opera GX\opera_crashreporter.exe"
            };
            var decisions = QuitTargeting.Select(Self(), new[] { SelfFact(), opera, reporter });
            var kills = Kills(decisions).ToArray();
            Assert.Single(kills);
            Assert.True(kills[0].IsSelf);
            Assert.Contains(decisions, decision => decision.Pid == 9560 && decision.Message.Contains("left running") && decision.Message.Contains("opera.exe"));
            Assert.Contains(decisions, decision => decision.Pid == 47368 && !decision.Kill);
        }

        [Fact]
        public void IgnoredChildTextDoesNotAuthorizeTermination()
        {
            var text = QuitTargeting.IgnoredDirectChildMessage(9560, "opera.exe");
            Assert.Contains("left running", text);
            Assert.Contains("not kill signals", text);
            Assert.DoesNotContain("kill", text.Substring(0, 6));
            Assert.True(QuitTargeting.IsExplicitlyProtected("opera_crashreporter.exe"));
            Assert.True(QuitTargeting.IsExplicitlyProtected("gameoverlayui.exe"));
            Assert.False(QuitTargeting.IsExplicitlyProtected("UnityCrashHandler64.exe"));
            Assert.False(QuitTargeting.IsExplicitlyProtected("REPO.exe"));
        }

        [Fact]
        public void DoesNotSelectDeniedProcessesEvenIfTheyAreChildren()
        {
            var names = new[]
            {
                "steam.exe", "steamwebhelper.exe", "vrserver.exe", "vrcompositor.exe", "vrmonitor.exe",
                "java.exe", "javaw.exe", "playit.exe", "conhost.exe"
            };
            var facts = new List<ProcessFact> { SelfFact() };
            for (var i = 0; i < names.Length; i++)
            {
                facts.Add(new ProcessFact
                {
                    Pid = 200 + i,
                    ParentPid = 30496,
                    ParentCreationFileTime = SelfCreated,
                    CreationFileTime = SelfCreated + 5,
                    ImagePath = @"C:\Program Files (x86)\Steam\steamapps\common\REPO\" + names[i]
                });
            }

            var kills = Kills(QuitTargeting.Select(Self(), facts)).ToArray();
            Assert.Single(kills);
            Assert.True(kills[0].IsSelf);
        }

        [Fact]
        public void DoesNotSelectHelperWithoutAVerifiedSelf()
        {
            var handler = new ProcessFact
            {
                Pid = 47536,
                ParentPid = 30496,
                ParentCreationFileTime = SelfCreated,
                CreationFileTime = SelfCreated + 20,
                ImagePath = HandlerPath
            };
            Assert.Empty(Kills(QuitTargeting.Select(Self(), new[] { handler })));
        }

        [Fact]
        public void DoesNotSelectProcessWithNoImagePath()
        {
            var blank = SelfFact();
            blank.ImagePath = null;
            Assert.Empty(Kills(QuitTargeting.Select(Self(), new[] { blank })));
        }

        [Theory]
        [InlineData("MenuPageMain", "ButtonEventQuit", true)]
        [InlineData("MenuPageEsc", "ButtonEventQuit", true)]
        [InlineData("MenuPageEsc", "ButtonEventQuitToMenu", false)]
        [InlineData("MenuPageMain", "ButtonEventQuitToMenu", false)]
        [InlineData("SteamManager", "ButtonEventQuit", false)]
        [InlineData("MenuPageEsc", "ButtonEventQuitGame", false)]
        public void InGameQuitFilterMatchesOnlyTheTwoQuitButtons(string typeName, string methodName, bool expected)
        {
            Assert.Equal(expected, QuitButtons.IsInGameQuitMethod(typeName, methodName));
        }

        [Fact]
        public void InGameGraceRequestsUnityQuitOnceAndDoesNotKillYet()
        {
            var session = new QuitSession();
            var start = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
            Assert.True(session.TryArmInGameQuit("MenuPageMain.ButtonEventQuit", start));
            Assert.False(session.TryArmInGameQuit("MenuPageEsc.ButtonEventQuit", start.AddSeconds(1)));

            var early = session.Tick(start.AddSeconds(3));
            Assert.False(early.CallApplicationQuit);
            Assert.False(early.Kill);

            var due = session.Tick(start.Add(QuitSession.InGameGrace));
            Assert.True(due.CallApplicationQuit);
            Assert.False(due.Kill);

            var again = session.Tick(start.Add(QuitSession.InGameGrace).AddSeconds(1));
            Assert.False(again.CallApplicationQuit);
            Assert.False(again.Kill);
        }

        [Fact]
        public void UnityQuitArmsASingleKillAfterTheShutdownBound()
        {
            var session = new QuitSession();
            var start = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
            session.TryArmInGameQuit("MenuPageEsc.ButtonEventQuit", start);
            Assert.True(session.TryNoteUnityWantsToQuit("Application.wantsToQuit", start.AddSeconds(1)));
            Assert.False(session.TryNoteUnityWantsToQuit("Application.wantsToQuit", start.AddSeconds(2)));

            var before = session.Tick(start.AddSeconds(1).Add(QuitSession.ShutdownBound).AddMilliseconds(-1));
            Assert.False(before.CallApplicationQuit);
            Assert.False(before.Kill);

            var due = session.Tick(start.AddSeconds(1).Add(QuitSession.ShutdownBound));
            Assert.True(due.Kill);
            Assert.True(due.AllowMainThreadSteamShutdown);
            Assert.False(session.Tick(start.AddMinutes(1)).Kill);
        }

        [Fact]
        public void MissingUnityCallbackKillsOnceAfterGracePlusBound()
        {
            var session = new QuitSession();
            var start = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
            session.TryArmInGameQuit("MenuPageMain.ButtonEventQuit", start);
            Assert.True(session.Tick(start.Add(QuitSession.InGameGrace)).CallApplicationQuit);

            var dueAt = start.Add(QuitSession.InGameGrace).Add(QuitSession.ShutdownBound);
            var due = session.Tick(dueAt);
            Assert.True(due.Kill);
            Assert.True(due.AllowMainThreadSteamShutdown);
            Assert.Contains("wantsToQuit", due.Reason);
        }

        [Fact]
        public void HardCloseBackupDoesNotCallSteamAndDoesNotWaitForGrace()
        {
            var session = new QuitSession();
            var start = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
            session.TryArmInGameQuit("MenuPageMain.ButtonEventQuit", start);
            session.NoteHardClose("game window close", start.AddMilliseconds(10));

            var during = session.Tick(start.AddSeconds(1));
            Assert.False(during.Kill);
            Assert.False(during.CallApplicationQuit);

            var backup = session.Tick(start.AddMilliseconds(10).Add(QuitSession.HardCloseBackup));
            Assert.True(backup.Kill);
            Assert.False(backup.AllowMainThreadSteamShutdown);
            Assert.False(backup.CallApplicationQuit);
        }

        [Fact]
        public void IdleAndQuitToMenuDoNotArmAKill()
        {
            var session = new QuitSession();
            var start = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
            Assert.False(QuitButtons.IsInGameQuitMethod("MenuPageEsc", "ButtonEventQuitToMenu"));
            var tick = session.Tick(start.AddMinutes(10));
            Assert.False(tick.Kill);
            Assert.False(tick.CallApplicationQuit);
        }

        [Fact]
        public void SteamShutdownRunsOnlyForAMainThreadForceStopWhileApiIsValid()
        {
            var call = SteamShutdown.Decide(new SteamShutdownRequest
            {
                ForceStopInProgress = true,
                OnMainThread = true,
                AlreadyCalledByThisPlugin = false,
                ApiStillValid = true
            });
            Assert.True(call.Call);

            Assert.False(SteamShutdown.Decide(new SteamShutdownRequest
            {
                ForceStopInProgress = false,
                OnMainThread = true,
                ApiStillValid = true
            }).Call);

            Assert.False(SteamShutdown.Decide(new SteamShutdownRequest
            {
                ForceStopInProgress = true,
                OnMainThread = false,
                ApiStillValid = true
            }).Call);

            Assert.False(SteamShutdown.Decide(new SteamShutdownRequest
            {
                ForceStopInProgress = true,
                OnMainThread = true,
                AlreadyCalledByThisPlugin = true,
                ApiStillValid = true
            }).Call);

            Assert.False(SteamShutdown.Decide(new SteamShutdownRequest
            {
                ForceStopInProgress = true,
                OnMainThread = true,
                ApiStillValid = false
            }).Call);
        }

        [Fact]
        public void SteamPolicyDoesNotUseRegistryOrAnExitCodeSignal()
        {
            Assert.False(SteamShutdown.WritesSteamRegistry);
            Assert.False(SteamShutdown.UsesExitCodeAsSteamSignal);
            Assert.False(SteamShutdown.ShutdownDetachesTrackedProcesses);
        }

        [Fact]
        public void ProductionSourcesDoNotSpoofSteamStateOrKillByProcessName()
        {
            var root = RepoRoot();
            var files = new[]
            {
                "RepoForceQuit.cs",
                "QuitButtons.cs",
                "QuitSession.cs",
                "QuitTargeting.cs",
                "SteamShutdown.cs",
                "WindowsProcessInspector.cs"
            };
            var text = string.Join("\n", files.Select(file => File.ReadAllText(Path.Combine(root, file))));
            Assert.DoesNotContain("GetProcessesByName", text);
            Assert.DoesNotContain("RunningAppID", text);
            Assert.DoesNotContain("Adding process", text);
            Assert.DoesNotContain("Microsoft.Win32", text);
            Assert.DoesNotContain("RegSetValue", text);
            Assert.DoesNotContain("BeginForceStop(tick.Reason, true)", text);
            Assert.Contains("BeginForceStop(tick.Reason, false)", text);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "RepoForceQuit.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            throw new InvalidOperationException("Could not find the repository root from the test output directory.");
        }
    }
}
