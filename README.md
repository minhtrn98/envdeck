# EnvDeck

Profile-based manager for **Windows environment variables** — a native Windows window plus an `envdeck` CLI.

Working on several projects that each need their own `JAVA_HOME`, SDK paths, `PATH` entries or tokens?
Put each set in a JSON **profile**, turn it **on** when you need it and **off** when you don't. Turning a
profile off restores exactly what it changed, even if other profiles or installers touched the same variables
in the meantime.

- **Profiles as JSON** — one readable file per profile, with a JSON schema for autocomplete in VS Code.
- **On / off** — writes to the registry (User or Machine scope) and notifies running apps; *groups* make
  profiles mutually exclusive (turn on `java21` and `java17` turns off).
- **PATH-aware** — `Prepend` / `Append` operations add entries to list variables instead of overwriting them.
- **Versioned** — every save is a new version; diff any two versions and roll back. Hand edits made in another
  editor are picked up as new versions automatically.
- **Edit the live environment** — add, edit, delete variables and `PATH` entries in place; changes wait for
  *Apply* (preview + automatic backup). What a profile that is on manages is locked — edit it in the profile.
- **Group related** — one button puts similar names next to each other (`redis_url`, `redis_pw`, `acc_redis`:
  names are split on `_`, `__` and `-`, earlier parts count more); tables also sort by Name / Value.
- **Backup & restore** — automatic backup before every change, manual snapshots, restore with preview.
- **Secrets stay hidden** — values of variables that look like secrets (`*_PASSWORD`, `*_TOKEN`, `*ApiKey`,
  `ConnectionStrings__*`...) are masked in every table, search/compare result and preview; an eye button shows one,
  the context menu marks or unmarks a name, and a copied secret is wiped from the clipboard after 30 s.
- **Move machines / reinstall Windows** — export everything (profiles + history + backups) into one
  password-protected `.envdeck` file (AES-256-GCM, PBKDF2-SHA256; a forgotten password cannot be recovered; a plain
  `.zip` is still possible but unencrypted), import on the new machine; `--portable` capture turns `C:\Users\you\...` into `%USERPROFILE%\...`.
- **Search** variables by name/value (text or regex) across profiles, the live environment and backups.
- **Compare** profiles, versions, the live environment and backups, entry by entry for `PATH`.
- **Doctor** — missing or duplicate `PATH` folders, broken profiles, profiles that drifted.
- **Session only** — `envdeck shell <profile> | Invoke-Expression` loads a profile into one terminal without
  touching the registry.
- **English / Tiếng Việt** — the window follows the Windows display language; switch any time in Settings.

## Install

```powershell
winget install minhtrn98.EnvDeck     # once published; pulls in the .NET 10 Desktop Runtime
```

Or download the installer / portable zip from the Releases page. The installer is per-user (no admin prompt)
and adds `envdeck` to your `PATH`.

## Quick start (CLI)

```powershell
envdeck new java17 --group java --desc "JDK 17 for project A"
envdeck set java17 JAVA_HOME "C:\jdk\17"
envdeck set java17 Path "%JAVA_HOME%\bin" --op prepend

envdeck new java21 --from java17
envdeck set java21 JAVA_HOME "C:\jdk\21"

envdeck apply java17 --dry-run     # preview
envdeck apply java17               # on
envdeck apply java21               # java17 goes off automatically (same group)
envdeck status
envdeck off java21                 # back to exactly how it was before

envdeck diff java17 java21         # compare profiles
envdeck diff java17@1 java17       # compare versions
envdeck history java17
envdeck rollback java17 1

envdeck capture go GOPATH GOROOT --portable   # turn existing variables into a profile
envdeck search jdk --in profiles,env
envdeck backup --note "before reinstalling Windows"
envdeck restore latest --dry-run
envdeck export D:\envdeck.zip --with-history --with-backups
envdeck import D:\envdeck.zip
envdeck doctor
envdeck ui                         # open the window
```

`envdeck help <command>` shows every option. Exit codes: `0` ok, `1` error, `2` usage, `3` needs administrator.

## Profile format

`%USERPROFILE%\.envdeck\profiles\java17.json`:

```jsonc
{
  "$schema": "../profile.schema.json",
  "name": "java17",
  "description": "JDK 17 for project A",
  "group": "java",
  "version": 3,
  "variables": [
    { "name": "JAVA_HOME", "value": "C:\\jdk\\17" },
    { "name": "Path", "value": "%JAVA_HOME%\\bin", "op": "Prepend" },
    { "name": "MAVEN_OPTS", "value": "-Xmx2g", "scope": "Machine" },
    { "name": "OLD_SDK_HOME", "op": "Remove" }
  ]
}
```

| Field   | Values                                   | Default | Meaning |
|---------|------------------------------------------|---------|---------|
| `scope` | `User`, `Machine`                        | `User`  | `HKCU\Environment` or `HKLM\…\Session Manager\Environment` (admin) |
| `op`    | `Set`, `Remove`, `Prepend`, `Append`     | `Set`   | Prepend/Append take `;`-separated entries and never duplicate them |
| `kind`  | `String`, `ExpandString`                 | auto    | Registry type; `%VAR%` references make it `ExpandString` automatically |

## How on/off stays safe

Applying a profile records a journal entry per variable (value before, value written, list entries added) in
`%LOCALAPPDATA%\EnvDeck\state.json`. Turning it off:

- restores the previous value if nobody changed the variable since;
- for `PATH`-like variables, removes only the entries the profile added (an installer's later additions stay);
- if another profile turned on later changed the same variable, hands the original value over to that
  profile's journal instead of clobbering it;
- otherwise leaves the variable alone and warns (`envdeck status` / `doctor` show such drift).

A backup is taken before every apply, unapply and restore.

Things Windows does that are worth knowing:

- Changes reach **new** processes. Already-open terminals keep their old environment — open a new one, or
  use `envdeck shell <profile> | Invoke-Expression` for the current session.
- Windows puts the **Machine `Path` before the User `Path`**. A User-scope `Prepend` cannot override a tool
  that is also on the Machine `Path`; use `"scope": "Machine"` for that (needs admin; Windows 11 `sudo` works:
  `sudo envdeck apply java17`).

## Where data lives

| What | Where | Sync between machines? |
|------|-------|------------------------|
| Profiles, history, backups | `%USERPROFILE%\.envdeck` (change with `envdeck config dataDir <folder>` or `ENVDECK_HOME`) | yes — point it at OneDrive or a git repo |
| Which profiles are on, settings | `%LOCALAPPDATA%\EnvDeck` | no (machine specific) |

## Build from source

Requires the .NET 10 SDK on Windows. Debug builds run through `dotnet.exe` (no `.exe` apphost), so `dotnet run`
also works on machines whose execution policy blocks freshly built, unsigned executables.

```powershell
dotnet build EnvDeck.slnx
dotnet test EnvDeck.slnx

# Try everything without touching your real environment:
$env:ENVDECK_SANDBOX = '1'      # registry writes go to HKCU\Software\EnvDeck\Sandbox
dotnet run --project src/EnvDeck.Cli -- list
dotnet run --project src/EnvDeck.App

./scripts/publish.ps1 -Version 0.1.0   # artifacts/dist: portable zip (+ installer if Inno Setup is installed)
```

```
src/EnvDeck.Core   profiles, versioning, apply/undo journal, backups, diff, search, bundles (no UI)
src/EnvDeck.Cli    envdeck.exe
src/EnvDeck.App    EnvDeck.App.exe — WPF with the Windows 11 Fluent theme
tests/             xUnit tests against an in-memory environment
packaging/         Inno Setup script and winget manifest templates
```

## Publishing to winget

The package is `minhtrn98.EnvDeck`; `packaging/winget` holds the manifests of the version submitted to
`microsoft/winget-pkgs`. The installer manifest must keep `Dependencies` (.NET 10 Desktop Runtime): winget runs
the installer silently, so the installer's own runtime check cannot warn the user.

1. Bump `<Version>` in `Directory.Build.props`, then push a tag (`git tag v0.1.2 && git push --tags`); the Release
   workflow builds the zip and the x64/arm64 installers and creates the GitHub release.
2. Later releases, once the first version is merged: set the repository variable `WINGET_ENABLED=true` and the
   secret `WINGET_TOKEN`; the workflow opens the winget PR for you (it copies the previous manifests, so the
   dependency carries over).

## Installing with Scoop

Add this repository as a bucket and install EnvDeck:

```powershell
scoop bucket add envdeck https://github.com/minhtrn98/envdeck
scoop install envdeck
```

The bucket manifest supports x64 and ARM64 and provides both the `envdeck` CLI and a Start menu shortcut.
EnvDeck requires the .NET 10 Desktop Runtime; install it with
`winget install Microsoft.DotNet.DesktopRuntime.10` if it is not already present.

The manifest is in `bucket/EnvDeck.json`. When publishing a new version, update its version, download URLs,
and SHA256 hashes from the release ZIPs so Scoop users can install the new release.

## License

MIT
