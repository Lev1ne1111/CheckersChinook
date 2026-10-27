# Checkers best-move API: installation on Windows Server (IIS)

Run every command in an **elevated** PowerShell (Run as Administrator), from the folder with
this README. If scripts are blocked, first run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
```

## 1. .NET 10 SDK

Install the **.NET 10 SDK** from https://dotnet.microsoft.com/download/dotnet/10.0. It is used to build the project.

## 2. IIS

```powershell
.\deploy\iis-setup.ps1 -InstallFeatures
```

This turns on IIS, Application Initialization and IIS Manager. The script then stops with
"ASP.NET Core Module V2 not found". That is expected: step 3 fixes it.

## 3. ASP.NET Core Hosting Bundle

On https://dotnet.microsoft.com/download/dotnet/10.0, under **ASP.NET Core Runtime 10.0.x** →
Windows, download **Hosting Bundle** (`dotnet-hosting-10.0.x-win.exe`, not the x64 runtime
installer) and install it. Install it **after** IIS (step 2): otherwise IIS does not get the
module, and the bundle has to be repaired. Then:

```powershell
iisreset
```

## 4. Engine and endgame databases

```powershell
.\setup.ps1 -EnginePath C:\engines\checkerboard -DbPath D:\tb\chinook
```

- Downloads CheckerBoard and the KingsRow engine from the author's site
  (https://edgilbert.org) and installs them to `C:\engines\checkerboard`.
- Downloads the Chinook 2–8 piece endgame databases, about 5.5 GB, to `D:\tb\chinook`.
- The download comes from web.archive.org, which sometimes refuses with `503` after a few GB.
  Run the same command again later: finished archives are skipped, and the download continues
  where it stopped. The summary at the end must list no `[fail]` lines.

Chinook endgame databases © Jonathan Schaeffer et al., University of Alberta (free to use, not
for sale, credit required). KingsRow and CheckerBoard © Ed Gilbert / Martin Fierz.

## 5. Publish the API to IIS

```powershell
.\deploy\iis-setup.ps1 -EnginePath C:\engines\checkerboard\engines\Kingsrow64.dll -DbPath D:\tb\chinook -Port 8080
```

The script:
- publishes the API to `C:\inetpub\checkers`;
- creates the app pool `CheckersApi` (No Managed Code, AlwaysRunning, no idle timeout,
  no periodic recycle) and the site `CheckersApi` on port 8080, with preload enabled;
- gives the pool read access to the engine and the databases, and write access to `logs\`;
- waits until `/healthz` reports ok.

It must end with:

```
[ok]   http://localhost:8080/healthz -> ok, workers 2/2, Kingsrow(x64) 1.20, DB 8 pieces
```

Run the same command again to update the site after a code change. Add `-SkipPublish` to only
restart the pool and reapply its settings, for example after adding database files.

## 6. Check

```powershell
.\scripts\acceptance.ps1 -BaseUrl http://localhost:8080
```

It must end with `All checks passed`. The checks are health, endgame positions under 50 ms,
"strong" midgame under 600 ms with a legal move, 422 for an invalid PDN, and 504 on timeout.

The test board is at http://localhost:8080/.

## If it does not start

- **500.19:** the Hosting Bundle is missing or was installed before IIS. Repair it
  (Apps → the Hosting Bundle → Modify → Repair), run `iisreset`, then repeat step 5.
- **500.30, or `/healthz` shows `workers 0/2`:** open Event Viewer → Windows Logs → Application.
  The "worker failed to start" entry ends with the worker's own output, which names the cause,
  usually a wrong engine path or a missing database folder.
- **Port 8080 is taken:** repeat step 5 with another `-Port`.
