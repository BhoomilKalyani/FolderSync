# Folder Sync

A small Windows app that mirrors one folder into **several destinations at once** — for example
your Documents folder into your **Google Drive**, **iCloud Drive** and an external backup drive,
all from a single click or on a schedule.

It works with cloud storage without any logins or API keys: OneDrive, iCloud Drive and Google
Drive for Desktop each keep a normal **local folder** on your PC that their own app syncs to the
cloud. Folder Sync just copies between those local folders, and each cloud client uploads from
there. That makes it fast and reliable, and it keeps working even if a provider changes its API.

Built with .NET 8 / WPF, using Windows' own **robocopy** for the file transfer and
**Task Scheduler** for automation.

---

## Features

| | |
|---|---|
| **Many destinations** | Sync one source to as many folders as you like; enable/disable each one individually. |
| **Per-destination mode** | Each destination is Mirror (an exact copy) or Additive (only adds and updates) — mirror to the cloud while a backup drive keeps everything. |
| **Deletion safety net** | For mirrored destinations, files that would be deleted are moved to a dated recycle folder instead, and can be restored from the app. |
| **Preview (dry run)** | See exactly what *would* be copied or deleted before committing to it. |
| **Scheduling** | Daily, monthly, and **at sign-in / every screen unlock** — no admin rights needed. |
| **Email notifications** | Optional summary email after each scheduled sync, with the log attached. |
| **Health check** | One button that diagnoses everything that can silently stop a sync. |
| **System tray** | Runs quietly in the tray with completion notifications. |
| **Skip lists** | Exclude file patterns and folders. |
| **Auto-update** | Notifies you when a new version is released and installs it in one click. |

---

## Install

1. Download **`FolderSyncSetup-<version>.exe`** from the
   [**Releases**](https://github.com/BhoomilKalyani/FolderSync/releases) page.
2. Run it. On first launch, set your **Source** folder and add your **destinations**.

**Requirements:** Windows 10 or 11 (64-bit). Nothing else — the .NET runtime is bundled.

- Installs **per-user** to `%LOCALAPPDATA%\Programs\FolderSync`, so there is **no UAC prompt**.
- Adds a Start Menu entry, an optional desktop shortcut, and an **Add/Remove Programs** entry.
- Setup can turn on **sync at sign-in and unlock** for you.
- Your settings live in `%LOCALAPPDATA%\FolderSync\config.json`. **Updates keep them**;
  uninstalling leaves them in place too, so reinstalling picks up where you left off.

> **SmartScreen:** Setup is not code-signed, so Windows may show *"Windows protected your PC"* the
> first time. Choose **More info → Run anyway**. (Removing that warning requires a paid
> code-signing certificate.)

---

## Getting started

A fresh install starts empty — nothing is synced until you say so.

1. **Source** — click **Browse…** and pick the folder you want to copy *from*.
2. **Destinations** — click **Add Destination…** for each folder to copy *to*. Typical choices:

   | Target | Typical local folder |
   |---|---|
   | Google Drive | `G:\My Drive\…` (drive letter varies) |
   | iCloud Drive | `%USERPROFILE%\iCloudDrive\…` |
   | OneDrive | `%USERPROFILE%\OneDrive\…` |
   | External / backup drive | `D:\…` |

3. Click **Preview** to see what would happen, then **Sync Now** to do it.
4. Optionally tick **Enable automatic scheduling** and turn on the triggers you want.

> **About Google Drive:** use a folder under **`My Drive`**. The *"Other computers"* section is a
> read-only view of backups from other machines and does not upload changes.
>
> **About iCloud:** it must be reachable as a local folder (iCloud for Windows installed). There is
> no public API for iCloud Drive, so a local folder is the only supported route.

Everything you edit saves immediately — there is no Save button.

---

## Sync modes

Every destination picks its own mode in the **Mode** column, so one source can feed a mirrored
cloud folder and an add-only archive at the same time.

| Mode | Behaviour |
|---|---|
| **Additive** (default for a new destination) | Only copies new and changed files. Never deletes anything. |
| **Mirror** | Keeps the destination identical to the source, so files you delete are removed there too. |

New destinations start as **Additive** deliberately: nothing can be lost until you opt in to
mirroring.

### Recycled files

Because mirroring propagates deletions, anything a mirror is about to delete is **moved aside**
first rather than deleted:

```
<destination>\_FolderSyncTrash\<yyyyMMdd-HHmmss>\<original relative path>
```

The **Recycled files** page lists these batches, shows what is inside, and restores files back into
the source folder — the next sync then copies them out to every destination again. That folder is
never mirrored (otherwise it would be purged itself) and batches older than
`RecycleRetentionDays` (default **30**) are removed automatically. Set
`"RecycleDeletedFiles": false` to make deletions permanent instead.

---

## Scheduling

Tick **Enable automatic scheduling**, then per row use **Enable** / **Disable** / **Remove**.
Buttons grey out to show the current state.

| Trigger | When it runs |
|---|---|
| **Daily** | Every day at the chosen time. |
| **Monthly** | On the chosen day of each month. |
| **Logon / unlock** | When you sign in **and every time you unlock the screen**. |

On a laptop that sleeps and locks rather than shutting down, **logon/unlock** is the trigger that
actually keeps things current. Because unlocking happens often, those runs are throttled to at most
one sync every `MinMinutesBetweenAutoSyncs` minutes (default **15**); skips are noted in
`logs\events.log`.

All three are ordinary **Task Scheduler** tasks running as your user with **no admin rights**.
They are registered from XML rather than `schtasks /SC …` because that is the only way to get:

- a **Session-Unlock** trigger (`/SC ONLOGON` cannot express it, and is refused without admin);
- `DisallowStartIfOnBatteries = false` — the command-line default silently **skips runs on
  battery**, which is fatal on a laptop;
- `StartWhenAvailable = true` — a run missed while the machine was asleep happens once it wakes.

---

## Email notifications

Optional: get a summary (with the run's log attached) after each **scheduled** sync. Manual runs
don't email — use **Send Test Email** to check your settings.

For Gmail you need a **16-character App Password**, not your normal password:

1. Turn on 2-Step Verification: <https://myaccount.google.com/signinoptions/twosv>
2. Create an App Password (app = *Mail*): <https://myaccount.google.com/apppasswords>
3. Paste the code into **App password** (spaces are fine) and click **Send Test Email**.

Each Google account has its own App Password and you can revoke it at any time. The password is
stored **DPAPI-encrypted** for your Windows user — never in plain text. Other providers work too:
change the SMTP host/port (587 = STARTTLS, 465 = SSL). Corporate Microsoft 365 tenants often block
SMTP sign-in, so a personal account is usually the more reliable sender.

---

## When something isn't working

Click **Check Health**. It reports `OK` / `WARN` / `FAIL` for the source, every destination
(including free space), each schedule **and whether its program file still exists**, how long ago
the last sync ran, email setup, and log writability.

Common causes:

| Symptom | Cause |
|---|---|
| A destination is skipped | Its drive isn't mounted — the cloud client isn't running. |
| Schedule shows enabled but never runs | It points at a program file that has moved. The app offers a one-click repair on startup. |
| Nothing syncs at startup | Windows only fires a *logon* trigger on a real sign-in; add the **unlock** trigger. |
| Email fails with `5.7.9` | You used your account password instead of an App Password. |

Logs live in `%LOCALAPPDATA%\FolderSync\logs` (**Open log folder** in the app): one
`sync-<timestamp>.log` per run, `preview-*.log` for dry runs, and `events.log` for schedule/update
events. Sync and preview logs are pruned after 7 days.

---

## Updates

The app checks GitHub for a newer release on startup, at most **once a day**. If one exists you get
a tray notification and an **Update to x.y.z** link; the dialog shows the release notes and an
**Update Now** button that downloads the installer, runs it, and restarts the app. You can also
check any time from the tray menu. Disable with `"CheckForUpdates": false`.

> The install path stays the same across versions on purpose. The scheduled tasks store an absolute
> path to `FolderSync.exe`, so a versioned install folder (as ClickOnce uses) would break them on
> every update. Setup also re-points any schedule left behind by an older install.

---

## Configuration reference

`%LOCALAPPDATA%\FolderSync\config.json` — the UI writes this, but you can edit it directly
(**Open config file**).

| Key | Default | Meaning |
|---|---|---|
| `Source` | `""` | Folder to copy from. |
| `Targets` | `[]` | Destinations: `Name`, `Destination`, `Enabled`, `Mirror` (`true` = mirror, `false` = additive). |
| `Mirror` | `true` | Legacy global mode. Only used to fill in destinations saved before per-destination modes existed. |
| `RecycleDeletedFiles` | `true` | Move would-be-deleted files aside instead of deleting. |
| `RecycleRetentionDays` | `30` | How long to keep recycled batches. |
| `RecycleFolderName` | `_FolderSyncTrash` | Recycle folder name inside each destination. |
| `ExcludeFiles` | `~$*`, `Thumbs.db`, `desktop.ini`, `.DS_Store` | File patterns to skip (robocopy `/XF`). |
| `ExcludeFolders` | `[]` | Folders to skip (robocopy `/XD`). |
| `MinMinutesBetweenAutoSyncs` | `15` | Throttle for logon/unlock runs. |
| `CheckForUpdates` | `true` | Check GitHub for new releases at startup. |
| `UpdateRepo` | `BhoomilKalyani/FolderSync` | `owner/repo` that publishes releases. |
| `Email` | disabled | SMTP settings; password stored encrypted. |

### Command line

| Argument | Purpose |
|---|---|
| *(none)* | Open the window. |
| `--sync` | Sync silently and exit. Used by the scheduled tasks. |
| `--sync --auto` | Same, but honours the minimum-gap throttle (logon/unlock). |
| `--repair-schedules` | Re-point schedules at this copy of the app. Run by Setup. |
| `--register-logon` | Register the logon/unlock task. Run by Setup. |
| `--remove-schedules` | Remove all tasks. Run by the uninstaller. |

---

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) and, for the installer,
[Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

```powershell
dotnet build -c Release          # compile
dotnet run                       # run from source
.\build-installer.ps1            # produces dist\FolderSyncSetup-<version>.exe
```

### Releasing a new version

1. Bump `<Version>` in `FolderSync.csproj`, then commit.
2. `.\build-installer.ps1`
3. `git tag v1.1.0 && git push origin v1.1.0`
4. Create a GitHub **release** for that tag and attach the setup `.exe`.

Existing installs compare the release **tag** against their own version and download the first
`.exe` asset attached to it.

---

## How it works

```
        ┌──────────────┐
        │ Source folder │
        └──────┬────────┘
               │  robocopy /MIR (or /E), per destination
   ┌───────────┼───────────┬──────────────┐
   ▼           ▼           ▼              ▼
Google Drive  iCloud    OneDrive     Backup drive
 local folder  folder    folder        (D:\…)
   │           │           │
   └───────────┴───────────┘
      each cloud client uploads its own folder
```

- **robocopy** does the copying — it handles long paths, retries, multithreading and exact
  mirroring far better than hand-written copy code.
- Deletions in mirror mode are detected with a **list-only pass** first, so they can be moved to
  the recycle folder before the real mirror runs.
- Task Scheduler provides automation; the app itself doesn't need to stay running.

## Licence

MIT — see [LICENSE](LICENSE).
