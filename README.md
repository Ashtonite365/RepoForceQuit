# Repo Force Quit

A small BepInEx 5 plugin for [R.E.P.O.](https://store.steampowered.com/app/3241660) that force-kills leftover game processes when you:

- click the game window close button, or
- close the BepInEx console window (or press Ctrl+C there)

That makes Steam less likely to get stuck on "R.E.P.O. - Stopping" after Unity hangs on exit.

- Plugin id: `io.ashtonite.repo.forcequit`
- Version: 1.0.2
- Separate from [RepoXR FBT](https://github.com/Ashtonite365/RepoXR). This plugin does not modify RepoXR.

It kills processes named `REPO`, plus `UnityCrashHandler64` / `UnityCrashHandler32` only if they live under `steamapps\common\REPO`.

## Build

Building expects R.E.P.O. (with BepInEx) at the default Steam path used in `RepoForceQuit.csproj`:

`C:\Program Files (x86)\Steam\steamapps\common\REPO`

```
dotnet build RepoForceQuit.csproj --configuration Release
```

Copy `bin\Release\RepoForceQuit.dll` into `BepInEx\plugins`.
