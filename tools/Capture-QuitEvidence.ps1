# Records R.E.P.O. quit evidence. Does not kill processes and does not write Steam registry keys.
# This is not a Steam UI test. Closing Opera is a separate manual step and is not done here.
param(
    [string]$LogPath = "C:\Program Files (x86)\Steam\steamapps\common\REPO\BepInEx\LogOutput.log",
    [string]$OutFile = "",
    [int]$PollSeconds = 0
)

$ErrorActionPreference = "Continue"
if ([string]::IsNullOrWhiteSpace($OutFile)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutFile = Join-Path (Split-Path -Parent $PSCommandPath) "quit-evidence-$stamp.txt"
}

$names = @(
    "REPO.exe",
    "UnityCrashHandler64.exe",
    "UnityCrashHandler32.exe",
    "opera.exe",
    "opera_crashreporter.exe",
    "steam.exe",
    "steamwebhelper.exe",
    "vrserver.exe",
    "vrcompositor.exe",
    "vrmonitor.exe",
    "java.exe",
    "javaw.exe",
    "playit.exe",
    "conhost.exe",
    "gameoverlayui.exe"
)

function Write-Snapshot {
    param([System.IO.StreamWriter]$Writer)
    $Writer.WriteLine("---- $(Get-Date -Format o) ----")
    $Writer.WriteLine("Record only. Opera, Steam, SteamVR, Java, Playit, and conhost are listed so they can be told apart from R.E.P.O. They are not targets.")
    try {
        $filter = ($names | ForEach-Object { "Name='$(_)'" }) -join " OR "
        $rows = Get-CimInstance Win32_Process -Filter $filter
        if (-not $rows) {
            $Writer.WriteLine("(none of the named images are running)")
        } else {
            foreach ($row in $rows) {
                $loadedOverlay = "not-checked"
                $Writer.WriteLine(("pid={0} parent={1} name={2} created={3} path={4} overlayDll={5}" -f $row.ProcessId, $row.ParentProcessId, $row.Name, $row.CreationDate, $row.ExecutablePath, $loadedOverlay))
            }
        }
    } catch {
        $Writer.WriteLine("process query failed: $($_.Exception.Message)")
    }

    $Writer.WriteLine("-- Force Quit log lines --")
    if (Test-Path -LiteralPath $LogPath) {
        Get-Content -LiteralPath $LogPath -Tail 200 | Where-Object { $_ -match "Repo Force Quit|In-game Quit|SteamClient|wantsToQuit|Force stop|ignore child|Leave to Main Menu" } | ForEach-Object { $Writer.WriteLine($_) }
    } else {
        $Writer.WriteLine("log not found: $LogPath")
    }
    $Writer.WriteLine("")
}

$writer = New-Object System.IO.StreamWriter($OutFile, $false)
try {
    $writer.WriteLine("Repo Force Quit evidence capture")
    $writer.WriteLine("Does not terminate processes. Does not read or write HKCU\Software\Valve\Steam.")
    $writer.WriteLine("A loaded gameoverlayrenderer64.dll is not queried here; check modules separately if needed. Do not kill a process only because that DLL is loaded.")
    Write-Snapshot -Writer $writer
    $deadline = (Get-Date).AddSeconds([Math]::Max(0, $PollSeconds))
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        Write-Snapshot -Writer $writer
    }
} finally {
    $writer.Flush()
    $writer.Dispose()
}

Write-Output $OutFile
