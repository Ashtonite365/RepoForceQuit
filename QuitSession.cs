using System;

namespace RepoForceQuit
{
    internal struct SessionTick
    {
        public bool CallApplicationQuit;
        public bool Kill;
        public bool AllowMainThreadSteamShutdown;
        public string Reason;
    }

    internal sealed class QuitSession
    {
        public static readonly TimeSpan InGameGrace = TimeSpan.FromSeconds(4);
        public static readonly TimeSpan ShutdownBound = TimeSpan.FromSeconds(6);
        public static readonly TimeSpan HardCloseBackup = TimeSpan.FromSeconds(2);

        private readonly object _gate = new object();
        private DateTime? _inGameUtc;
        private string _inGameSource;
        private DateTime? _unityUtc;
        private string _unitySource;
        private bool _callApplicationQuitRaised;
        private bool _hardClose;
        private DateTime _hardUtc;
        private string _hardReason;
        private bool _killRaised;

        public bool TryArmInGameQuit(string source, DateTime utcNow)
        {
            lock (_gate)
            {
                if (_hardClose || _killRaised || _inGameUtc.HasValue || _unityUtc.HasValue)
                    return false;
                _inGameUtc = utcNow;
                _inGameSource = string.IsNullOrEmpty(source) ? "in-game Quit" : source;
                return true;
            }
        }

        public bool TryNoteUnityWantsToQuit(string source, DateTime utcNow)
        {
            lock (_gate)
            {
                if (_hardClose || _killRaised || _unityUtc.HasValue)
                    return false;
                _unityUtc = utcNow;
                _unitySource = string.IsNullOrEmpty(source) ? "Unity quit" : source;
                return true;
            }
        }

        public void NoteHardClose(string source, DateTime utcNow)
        {
            lock (_gate)
            {
                if (_hardClose)
                    return;
                _hardClose = true;
                _hardUtc = utcNow;
                _hardReason = string.IsNullOrEmpty(source) ? "hard close" : source;
            }
        }

        public void SuppressFurtherKills()
        {
            lock (_gate)
            {
                _killRaised = true;
            }
        }

        public SessionTick Tick(DateTime utcNow)
        {
            lock (_gate)
            {
                var result = new SessionTick();
                if (_killRaised)
                    return result;

                if (_hardClose)
                {
                    if (utcNow - _hardUtc < HardCloseBackup)
                        return result;
                    _killRaised = true;
                    result.Kill = true;
                    result.AllowMainThreadSteamShutdown = false;
                    result.Reason = _hardReason + " backup";
                    return result;
                }

                if (_inGameUtc.HasValue && !_unityUtc.HasValue && !_callApplicationQuitRaised &&
                    utcNow - _inGameUtc.Value >= InGameGrace)
                {
                    _callApplicationQuitRaised = true;
                    result.CallApplicationQuit = true;
                    result.Reason = "in-game grace elapsed before Unity quit (" + _inGameSource + ")";
                }

                if (_unityUtc.HasValue && utcNow - _unityUtc.Value >= ShutdownBound)
                {
                    _killRaised = true;
                    result.Kill = true;
                    result.AllowMainThreadSteamShutdown = true;
                    result.Reason = "Unity shutdown exceeded " + (int)ShutdownBound.TotalSeconds + "s (" + _unitySource + ")";
                    return result;
                }

                if (_callApplicationQuitRaised && !_unityUtc.HasValue && _inGameUtc.HasValue &&
                    utcNow - _inGameUtc.Value >= InGameGrace + ShutdownBound)
                {
                    _killRaised = true;
                    result.Kill = true;
                    result.AllowMainThreadSteamShutdown = true;
                    result.Reason = "Application.Quit did not reach wantsToQuit (" + _inGameSource + ")";
                }

                return result;
            }
        }
    }
}
