# Repo Force Quit

BepInEx 5 plugin for [R.E.P.O.](https://store.steampowered.com/app/3241660) (app id 3241660). Plugin id: `io.ashtonite.repo.forcequit`. Version: 1.0.3.

It does not modify RepoXR. It does not ship game DLLs.

## What 1.0.3 changes

In-game **Quit Game** is not the window X button. On the builds whose menu code is public, `MenuPageMain.ButtonEventQuit` and `MenuPageEsc.ButtonEventQuit` set `quitApplication` and start the outro. `Application.Quit` runs later, from `PlayerAvatar.OutroDone`, only if that flag is set on the local avatar. `MenuPageEsc.ButtonEventQuitToMenu` is a different path (`leavePhotonRoom`, log line `Leave to Main Menu`) and is not a desktop quit. That body is from a public decompile plus method names already patched by other mods. It has not been confirmed against the 0.5.1 binary, and no live in-game Quit click has been captured yet.

1.0.2 killed from `Application.wantsToQuit` and killed every process named `REPO`. The in-game button never reaches that callback until the outro finishes, and a kill inside `wantsToQuit` runs before `OnApplicationQuit` / `SteamManager.OnDestroy`. 1.0.3 does this instead:

- Postfix the two Quit Game methods and let the original method run.
- If Unity quit has not started after 4 seconds, call `Application.Quit` on the main thread so the game can still save and shut Steam down.
- `RunManager.OnApplicationQuit` calls `DataDirector.SaveDeleteCheck`. `SteamManager.OnDestroy` calls `CancelSteamAuthTicket` then `SteamClient.Shutdown` when it is the live instance. Those stay the happy path. This plugin does not call them unless it is about to force-stop the process.
- If the process is still alive 6 seconds after `Application.wantsToQuit`, or 6 seconds after a requested `Application.Quit` never reaches that callback, force-stop this instance.
- Window X and the BepInEx console control handler still force-stop immediately. The console window subclass stays one-shot, so a failed hook does not log every frame. The console control handler is still installed. That handler runs on a Windows-created thread, so it does not call the Steam API.

## How Steam decides the game stopped

Documented:

- [`SteamAPI_Shutdown`](https://partner.steamgames.com/doc/api/steam_api) releases Steamworks pointers and memory. Valve's text says to call it during process shutdown if possible, and says it does **not** unhook the overlay. The [API overview](https://partner.steamgames.com/doc/sdk/api) says Steam API calls are marshaled over a cross-process pipe (`ISteamClient::HSteamPipe`). Facepunch's `SteamClient.Shutdown` is the wrapper R.E.P.O. uses: if `IsValid` is false it returns; otherwise it stops its callback loop and calls `SteamAPI_Shutdown` ([source](https://github.com/Facepunch/Facepunch.Steamworks/blob/master/Facepunch.Steamworks/SteamClient.cs)). A second call is a no-op in that source. The copy shipped inside the game was not recompiled here.
- [`Application.quitting`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Application-quitting.html) is not raised when the player is forced to quit or crashes. [`Application.wantsToQuit`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Application-wantsToQuit.html) can still cancel a quit. `TerminateProcess` therefore does not run the game's `OnDestroy` shutdown.

Not a Valve spec page, but the statement used to design this plugin: Riley Labrecque (Steamworks.NET) wrote that `SteamAPI_Shutdown` does not by itself clear "running", and that Steam keeps the app running until the game process and the child processes Steam is tracking have exited ([issue 13](https://github.com/rlabrecque/Steamworks.NET/issues/13), [issue 187](https://github.com/rlabrecque/Steamworks.NET/issues/187)). Public client logs use lines of the form `Adding process <pid> for gameID <appid>` and later `Removing process` / `Game removed`. The slash-separated three-pid variant is **not** defined in the Steamworks API docs checked for this change. Do not treat that layout as a field specification.

No Steamworks page found tells the game to write `Running` or `RunningAppID`, or to use a particular process exit code, as the "stopped" signal. This plugin does not read or write Steam's registry. `TerminateProcess` is passed status 0 only so the force-stop is not labeled with a made-up crash code. That 0 is not a Steam status API. `SteamAPI_Shutdown` in this process does not detach other processes Steam has already added for app 3241660.

## Browsers Steam has tracked

A live note at 11:25 AEDT, not a proven cause: Steam's UI was gone, with `CSteamEngine::BMainLoop appears to have stalled` and `fatal stalled cross-thread pipe`. SteamVR processes were gone. At a 11:15:42 VR launch, `streaming_log.txt` recorded `Adding process 9560 / 47368 / 13028 for gameID 3241660`. Those process ids were later Opera GX (`opera.exe`, a child `opera.exe`, and `opera_crashreporter.exe` under Opera GX's folder). Three of them still had `gameoverlayrenderer64.dll` loaded. Their recorded parent pid was already dead and its image is unknown. Nothing here shows that parent was `REPO.exe`, that the overlay caused the pipe stall, or that the `Win32Event` already-existed console line is the same fault.

`gameoverlayrenderer64.dll` is Steam's overlay. Valve's [overlay doc](https://partner.steamgames.com/doc/features/overlay) says the overlay hooks a game launched from Steam, and `ISteamFriends::ActivateGameOverlayToWebPage` opens **Steam's** overlay browser. It does not say to inject Opera, and it does not say a loaded overlay DLL means a process may be killed. Windows can keep a child in a parent's job and can inherit handles when a process is created that way. That is general OS behavior, not a finding that these Opera processes inherited R.E.P.O.'s Steam pipe.

This plugin will not close Opera, Opera's crash reporter, Steam, SteamVR, or every process that has the overlay DLL loaded. A Steam tracking line is not a kill list. If Steam is still associating a live browser with app 3241660, shutting down the API inside `REPO.exe` does not remove that association. Closing the browser is a separate, manual check and only if you approve it. It is not part of this plugin.

## What a force-stop is allowed to end

Only this `REPO.exe`, checked by pid, creation time, and full image path, and a `UnityCrashHandler64.exe` or `UnityCrashHandler32.exe` whose parent is that same process, whose creation time is not earlier, and whose image sits in the same folder. Anything else that is a direct child is logged once and left running, including `opera.exe`. Exact names that are never killed include Opera, Steam, SteamVR, the overlay UI, Java, Playit, and `conhost.exe`. `opera_crashreporter.exe` contains the letters "repo" and is not a target. There is no substring match and no "every process named REPO" kill, so a second copy of the game is left alone. Pid reuse is rejected by comparing creation time immediately before terminate.

## Build on Windows

From `C:\Users\ashto\OneDrive\Desktop\Grok\RepoForceQuit`, with R.E.P.O. and BepInEx at `C:\Program Files (x86)\Steam\steamapps\common\REPO`:

```
%LOCALAPPDATA%\dotnet10\dotnet.exe test tests\RepoForceQuit.Tests.csproj -c Release
%LOCALAPPDATA%\dotnet10\dotnet.exe build RepoForceQuit.csproj -c Release
```

Copy `bin\Release\RepoForceQuit.dll` to `BepInEx\plugins`. Do not copy `0Harmony.dll`, Unity modules, or `Assembly-CSharp.dll`. The references stay `Private=false`.

The test project builds the targeting and shutdown rules as `netstandard2.1` and runs them on `net8.0`. It does not load the game. `RepoForceQuit.csproj` itself was not compiled in the environment that wrote this change, because the game assemblies are not on that machine.

## Still needs a live check

Not done: click in-game Quit Game, watch Steam's UI, and confirm which processes exit. The 11:09-11:12 captures ended around `Leave to Main Menu` or while the main menu was still open. They do not show the Quit Game button. 1.0.2 did hook the game window, did not hook the console window (the control handler remained, and it does not retry every frame), and did not log an in-game quit signal because that log line did not exist yet.

`tools\Capture-QuitEvidence.ps1` only records. It does not kill processes and it does not write Steam's registry. Run it while you press Quit Game. It will not tell you what the Steam client UI showed.

Evidence that would pin the browser question down, without this plugin killing anything:

- The image and start time of parent pid 700, from a process-creation trace or a Steam log line that names that parent.
- `streaming_log.txt` lines around `Adding process 9560 / 47368 / 13028`, plus any later `Removing process` or `Game removed` for app 3241660.
- Whether those Opera processes were started by `REPO.exe`, by Steam, or by hand.
- After a real Quit Game, with Opera left open and Steam's UI actually up: does Steam still show R.E.P.O. as running?
- The 1.0.3 log lines `In-game Quit observed`, `Unity wantsToQuit`, `SteamClient.Shutdown` or `Skipped SteamClient.Shutdown`, and `ignore child` / `kill pid`.
