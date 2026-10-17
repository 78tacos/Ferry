# Ferry for IT: deployment and silent migrations

This guide is for IT staff who move PCs for other people: at a tech bench, remotely, or in bulk. It covers what
Ferry needs, how to script it with `ferry-cli.exe`, how to map old accounts to new ones (domain to Microsoft Entra
ID, for example), and how to check the results.

For the click-through wizard, see the [user guide](user-guide.md). Everything the wizard does, the command line
can do too.

* [What you deploy](#what-you-deploy)
* [Requirements](#requirements)
* [Choosing a method](#choosing-a-method)
* [Plan files](#plan-files)
* [Mapping users](#mapping-users)
* [Recipes](#recipes)
* [Passwords and pairing codes](#passwords-and-pairing-codes)
* [Checking the result](#checking-the-result)
* [Undo from the command line](#undo-from-the-command-line)
* [Security notes](#security-notes)
* [Command reference](#command-reference)
* [Limitations](#limitations)

---

## What you deploy

`build\publish.ps1` produces `dist\Ferry-<version>\`:

| File | Size | Use |
|---|---|---|
| `Ferry.exe` | about 59 MB | The wizard |
| `ferry-cli.exe` | about 36 MB | Command line, for scripts and silent runs |

Both are self-contained single files. There's nothing to install and no .NET runtime is needed. Run them from a
local folder or a USB stick. Copy `ferry-cli.exe` to the PC before running it, rather than running it straight
from a network share. A run can take hours, and it shouldn't depend on the share staying reachable.

Neither file is code-signed yet. If you use application control (AppLocker, WDAC, Smart App Control), allow them
by hash or path, or sign them with your organization's certificate.

## Requirements

| | Needed for |
|---|---|
| **64-bit Windows 10 or 11** on both PCs | |
| **Elevation** (admin, or SYSTEM) | A full migration: other users' profiles, Wi-Fi passwords, printers, machine-wide settings, program installs, shadow copies, System Restore points. Without it, only the account running Ferry is moved and the rest is skipped. |
| **winget (App Installer)** on the new PC | Reinstalling programs. Ferry finds it under `Program Files\WindowsApps` too, so it works as SYSTEM. Without it, programs can't be reinstalled, and Ferry warns before starting. |
| **Internet access** on the new PC | winget downloads. |
| **A signed-in profile** on the new PC for each target account | Ferry only restores into profiles that exist. Have each person sign in once first. |
| **Free disk space** | The new PC needs room for everything selected. A migration file needs up to the full selected size, because photos and video don't compress. |

### Where Ferry keeps its data

| | Elevated | Not elevated |
|---|---|---|
| Undo data, reports, logs, temp files | `%ProgramData%\Ferry\` | `%LocalAppData%\Ferry\` |

Inside are `Undo\`, `Reports\`, `Logs\` and `Temp\`. Set the environment variable `FERRY_DATA_ROOT` to keep them
somewhere else, for example on the USB stick Ferry runs from.

### Network

| Port | Direction | Purpose |
|---|---|---|
| UDP 47420 | Old PC broadcasts; new PC answers | Finding new PCs on the same subnet (IPv4 broadcast). |
| TCP 47421 | Old PC → new PC | The transfer. Change it with `receive --port` and `send --to host:port`. |

While the new PC waits, an elevated Ferry adds a temporary inbound Windows Firewall rule for its own executable,
named **Ferry migration (temporary)**, and removes it afterwards. The old PC needs no inbound opening.

* **Policies that ignore local firewall rules.** If your Group Policy or Intune firewall profile turns off
  local rule merging, the temporary rule has no effect. Either allow `Ferry.exe` and `ferry-cli.exe` inbound on
  TCP 47421 (and UDP 47420 for discovery) by policy, or use migration files.
* **Different subnets or VLANs.** Discovery won't cross routers, so connect by name or address:
  `send --to NEWPC` or `--to 10.20.30.40`.
* **Guest Wi-Fi with client isolation, and VPNs**, usually block PC-to-PC connections.

### Security software

Speed depends on the number of files more than their size, because endpoint protection scans every file as it's
read and written. Ferry reads and writes many small files at once to hide this. In testing, a 3,700-file folder
saved in 9 seconds and restored in 18. Folders with 100,000+ tiny files (`node_modules`, package caches) still
run at around 200 files a second. Exclude them with `--exclude node_modules` or in the plan, and recreate them
on the new PC.

## Choosing a method

| Method | Good for | How |
|---|---|---|
| **Direct, over the network** | Both PCs side by side or on the same LAN | `receive` on the new PC, `send` on the old PC. Data is applied as it arrives, and programs install while files copy. |
| **Migration file on a share** | Tech bench, old PC collected before the new one is ready, different sites | `capture` to `\\server\share\...`, then `restore` on the new PC. |
| **Migration file on USB** | No network between the PCs | Same as above. FAT32 drives get 4 GB parts automatically. |

## Plan files

A plan is a JSON file holding a migration's choices, so the same choices apply on every PC. Make one in either
of these ways:

* **From the wizard:** pick what to move on the *Choose* screen, then click **Save choices…**. User SIDs are
  wildcarded, so the plan works on other PCs.
* **From the command line:** `ferry-cli plan --out plan.json` writes an example plan to edit, and
  `ferry-cli plan --out plan.json --from-scan` writes this PC's default choices.

Pass it with `--plan plan.json` to `scan`, `capture`, `send`, `receive` or `restore`. Command-line options add to
the plan: `--include`, `--skip`, `--exclude`, `--user` and `--folder` append to its lists, and `--apps` and
`--conflicts` override it.

### Example: a standard office migration

```json
{
  "include": ["defaults"],
  "skip": ["name:Downloads", "migrator:fonts", "name:*Steam*"],
  "exclude": ["*.iso", "*.vhdx", "node_modules", "Downloads\\*.exe"],
  "apps": "winget",
  "conflicts": "OverwriteIfNewer",
  "userMap": { "CONTOSO\\jsmith": "AzureAD\\JohnSmith" },
  "folderMap": { "D:\\Data": "C:\\Data" },
  "createRestorePoint": true,
  "closeApps": true,
  "useShadowCopy": true,
  "maxFileSizeBytes": 10737418240
}
```

### Fields

| Field | Type | Default | Meaning |
|---|---|---|---|
| `include` | list of rules | `[]` | Items to select. Empty means the usual defaults when scanning (`capture`, `send`, `receive`), and everything in the file for `restore`. |
| `skip` | list of rules | `[]` | Items to leave out. Applied after `include`. |
| `exclude` | list of patterns | `[]` | Files never to copy, inside any selected folder: `*.iso`, `node_modules`, `Downloads\*.exe`. A leading `\` anchors a pattern to the top of the folder. |
| `users` | list | `[]` (everyone) | Only scan these old-PC users, by account (`CONTOSO\jsmith`), user name or SID. Same as `--user`. |
| `extraFolders` | list of paths | `[]` | Folders outside user profiles to offer, such as `D:\Projects`. Same as `--folder`. |
| `apps` | `winget`, `copy` or `default` | `default` | How to move programs that offer a choice. `default` is winget where available. `copy` is experimental. |
| `modes` | map of item id → mode | `{}` | Per-program overrides, as saved from the wizard. |
| `conflicts` | `KeepBoth`, `OverwriteIfNewer`, `Overwrite`, `Skip` | `KeepBoth` | What to do when a file already exists on the new PC. Identical files are always skipped. |
| `userMap` | map old → new | `{}` | Which new-PC account gets whose data. See [Mapping users](#mapping-users). |
| `folderMap` | map old → new | `{}` | Where folders from outside user profiles go. By default a folder keeps its path if the new PC has that drive, otherwise it goes under `C:\From <OLDPC>\` (for example `D:\Projects` → `C:\From OLDPC\D\Projects`). |
| `createRestorePoint` | bool | `true` | Make a System Restore point first (elevated only; Windows allows one a day). |
| `closeApps` | bool | `false` | Close browsers, Outlook and Office if they're open when their data is replaced. Without this, the restore warns and that data can fail. |
| `useShadowCopy` | bool | `true` | Read files that are in use from a Volume Shadow Copy (elevated only). Same as leaving out `--no-shadow`. |
| `maxFileSizeBytes` | number | none | Skip files larger than this. |

### Selection rules

Rules in `include` and `skip` (and `--include` / `--skip`) match items in the inventory:

| Rule | Matches |
|---|---|
| `all` | Everything |
| `defaults` | What Ferry ticks by default |
| `category:<name>` | `WindowsSettings`, `Applications`, `AppSettings`, `BrowsersAndEmail`, `Files` |
| `migrator:<id>` | `files`, `folders`, `apps`, `appdata`, `browsers`, `office`, `wifi`, `printers`, `drives`, `personalization`, `environment`, `fonts`, `defaultapps` |
| `name:<glob>` | Item name, e.g. `name:*Chrome*`, `name:Downloads` |
| `group:<glob>` | Group (often the user), e.g. `group:*jsmith*` |
| `item:<id glob>` | Exact item ids, as listed by `ferry-cli scan --details` |

Run `ferry-cli scan --plan plan.json --details` on a representative PC to see exactly what a plan selects before you
use it anywhere.

## Mapping users

Each user from the old PC is matched to a profile on the new PC in this order:

1. an entry in `userMap` (or `--map OLD=NEW`), matched by account name, user name or SID on either side;
2. the same SID (a domain account moving to another domain-joined PC);
3. the same account name, then the same user name;
4. a user name that looks alike;
5. otherwise, whoever is signed in at the console.

Things to know:

* **Domain to Entra ID:** `CONTOSO\jsmith` and `AzureAD\JohnSmith` don't match by name. Map them explicitly:
  `--map "CONTOSO\jsmith=AzureAD\JohnSmith"`.
* **Several old users can land in one profile.** On a PC with several unmatched old users, step 5 sends them all to
  the signed-in person. Limit the scan with `--user` or `"users"`, or map each one.
* **Running as SYSTEM with nobody signed in**, step 5 has no one to pick, so unmatched users are skipped (with a
  warning). Map them explicitly.
* **The target profile must exist.** A `userMap` entry naming an account with no profile on the new PC stops the
  restore (exit code 3) with: *"…there is no profile for … on this PC. Sign in as that user once first."*
* Ferry prints the mapping before it starts, one line per old user (`CONTOSO\jsmith → AzureAD\JohnSmith`), and
  says which users won't be moved. Run with `--dry-run` first if you want to see it without changing anything.

Paths are rewritten for the new account: shortcuts, settings files and registry values that pointed into
`C:\Users\jsmith\` are changed to the new profile's folders, including folders redirected to OneDrive or another
drive.

## Recipes

Run every command from an elevated prompt, or as SYSTEM through your management tool.

### Tech bench: old PC to a share, then onto the new PC

On the **old PC**:

```powershell
$env:FERRY_PW = '<migration password>'
.\ferry-cli.exe capture --plan .\standard.json --out "\\fileserver\migrations$\$env:COMPUTERNAME.ferry" --password-env FERRY_PW
```

On the **new PC**, after the user has signed in once:

```powershell
$env:FERRY_PW = '<migration password>'
.\ferry-cli.exe info "\\fileserver\migrations$\OLDPC.ferry" --password-env FERRY_PW
.\ferry-cli.exe restore --in "\\fileserver\migrations$\OLDPC.ferry" --password-env FERRY_PW --plan .\standard.json --map "CONTOSO\jsmith=AzureAD\JohnSmith"
```

`info` shows what's in the file without changing anything. Delete the file from the share once the user has
signed off on the move.

### Direct, both PCs on the network

On the **new PC** (waits up to 60 minutes):

```powershell
.\ferry-cli.exe receive --plan .\standard.json --code 7KQM-X2RP-9TW4 --timeout 60 --map "CONTOSO\jsmith=AzureAD\JohnSmith"
```

On the **old PC**:

```powershell
.\ferry-cli.exe send --to NEWPC --code 7KQM-X2RP-9TW4
```

In a direct transfer the **new PC's** options decide what is moved and how: the plan's selection and
`exclude` patterns, `--map`, `--conflicts`, `--dry-run`. The old PC's `--plan`, `--user` and `--folder` only
decide what it scans. `send` exits with the new PC's result,
so either side's exit code tells you how it went.

Leave out `--code` on the new PC to get a random one-time code (shown on screen) that changes after every wrong try.

### Through Intune, Configuration Manager or another tool

* Ferry runs fine as SYSTEM. Read [Mapping users](#mapping-users) first, because there may be no console user to
  fall back on.
* Use migration files for remote runs. A direct transfer needs both scripts running at the same time.
* Give the job a long enough time limit. Large profiles take hours, and Ferry keeps the PC awake while it works
  (closing a laptop lid still sleeps it).
* Programs that only install per user can't always be installed for the person when Ferry runs as SYSTEM. Check
  the report's "Programs that weren't reinstalled" list and failed installs.
* Collect the exit code and the JSON report. See [Checking the result](#checking-the-result).

### Trying a plan safely

```powershell
.\ferry-cli.exe restore --in .\OLDPC.ferry --password-env FERRY_PW --dry-run
.\ferry-cli.exe restore --in .\OLDPC.ferry --password-env FERRY_PW --sandbox C:\FerryTest
```

* `--dry-run` goes through everything and reports what would happen, changing nothing.
* `--sandbox <dir>` really restores, but files go under `<dir>`, registry changes go under
  `HKCU\Software\FerrySandbox`, and program installs and system changes are only logged. Good for testing a plan
  on your own PC. Delete the folder and that registry key afterwards.

## Passwords and pairing codes

| Option | Notes |
|---|---|
| `--password-env VAR` | Read from an environment variable. Best for scripts. |
| `--password-file F` | Read from a file. Lock its permissions down and delete it afterwards. |
| `--password` | Prompt for it (twice when creating a file). Needs someone at the keyboard. |
| `--password <value>` | Works, but the password is visible to anyone who can list processes. Avoid it. |

* A file saved without a password still contains Wi-Fi passwords, browser data and Firefox saved passwords.
  Always set one, unless the file never leaves a PC you control.
* Restoring an encrypted file without a password option, when there's no console to type into, stops with exit
  code 5.
* A fixed pairing code (`receive --code`) needs at least 10 letters or digits; dashes, spaces and case are ignored.
  It doesn't change after a wrong try (each wrong try costs the guesser 2 seconds instead). Use a fresh random code
  for every migration and treat it like a password.

## Checking the result

### Exit codes

| Code | Meaning | What to do |
|---|---|---|
| `0` | Done | Nothing. Hand the PC over. |
| `2` | Done, but some items failed | Read the report's failures and "Still to do" list. |
| `3` | Stopped: a fatal error, a blocking preflight problem, or a missing profile | The console and report say why. Fix it and run again. Identical files already copied are skipped. |
| `4` | Cancelled (Ctrl+C, or the other PC cancelled) | What was restored so far stays and can be undone. |
| `5` | Password problem: wrong or missing | Supply the right password. |
| `64` | Bad arguments, including any unknown option | Fix the command line. A misspelt option is refused rather than ignored. |

### Reports

Every run writes an HTML report and a JSON copy of it to `Reports\` in Ferry's data folder, and prints the path
(`Report: …`). A `capture` also copies its report next to the migration file. The JSON has the same content as the
HTML:

| Field | Meaning |
|---|---|
| `kind` | `Capture`, `Restore` or `Undo` |
| `source`, `target` | Computer name, Windows version and join type of each PC |
| `cancelled`, `fatalError`, `dryRun` | How it ended |
| `items[]` | One per item: `name`, `group`, `category`, `status` (`Succeeded`, `Warnings`, `Failed`, `Skipped`), `filesDone`, `filesSkipped`, `filesFailed`, `bytesDone`, `messages[]` |
| `manualSteps[]` | The "Still to do" list for the user |
| `programsNotMoved[]` | Programs from the old PC that weren't reinstalled |
| `notes[]` | Things worth knowing, e.g. whether a restore point was made |
| `undoId` | The id to pass to `ferry-cli undo` |
| `logPath` | The log for this run |

For example, to list what failed on the last restore:

```powershell
$latest = Get-ChildItem "$env:ProgramData\Ferry\Reports\Ferry restore *.json" | Sort-Object LastWriteTime | Select-Object -Last 1
$report = Get-Content $latest.FullName -Raw | ConvertFrom-Json
$report.items | Where-Object status -eq 'Failed' | Select-Object name, group, @{n='why'; e={ $_.messages[-1].text }}
$report.manualSteps
```

### Logs

Each run writes a log to `Logs\` (`ferry-cli-<date>-<time>.log`, or `ferry-app-…` for the wizard). Add `--verbose` to
echo it to the console too. Use `--quiet` to show only the summary.

### Inventory without migrating

`ferry-cli scan --json inventory.json` saves everything Ferry found on a PC (users, folders and sizes,
programs and whether winget can reinstall them, Wi-Fi networks, printers, browsers) without copying anything.
It's useful for planning, or for spotting programs that will need a manual install.

## Undo from the command line

```powershell
.\ferry-cli.exe undo --list                      # every restore on this PC, newest first, with ids
.\ferry-cli.exe undo 20261017-101500-a1b2        # asks you to type YES
.\ferry-cli.exe undo 20261017-101500-a1b2 --yes  # no prompt, for scripts
.\ferry-cli.exe undo --discard 20261017-101500-a1b2
```

Undo reverses every recorded change, newest first:

* files created are deleted, **including later edits to them**;
* replaced files and folders come back from the backup;
* registry keys and values are put back;
* Wi-Fi profiles, printers and services Ferry added are removed;
* programs installed with winget are uninstalled.

If something can't be reversed, the rest still is. Run undo again to retry: steps already reversed are skipped.

Undo data keeps a copy of every file a restore replaced, so it can be large. Once the user is happy, free the space
with `--discard`, after which that restore can't be undone. Undo data lives with Ferry's data (see
[Where Ferry keeps its data](#where-ferry-keeps-its-data)). An elevated restore's undo data is in `%ProgramData%`,
so any administrator can undo it.

## Security notes

* **Migration files** can be encrypted with a password: PBKDF2-SHA256 with 600,000 iterations, then AES-256-GCM in
  authenticated segments. Tampering, reordering or cutting the file short is detected. Files without a password
  still carry a SHA-256 hash per file, to catch damage.
* **Network transfers** are always encrypted. An ephemeral ECDH (P-256) key exchange is mixed with a key derived
  from the pairing code. A wrong code fails before any data is sent, and someone listening on the network learns
  nothing. Each frame is AES-256-GCM.
* **What a file or transfer contains:** user files, Wi-Fi passwords, browser profiles (Firefox saved passwords
  included), Outlook `.pst` files and registry settings. Store migration files where only IT can read them, and
  delete them when you're done.
* **Firewall:** the only change is the temporary rule described under [Network](#network), removed when the new PC
  stops waiting.
* **winget installs** run with `--accept-package-agreements` and `--accept-source-agreements`. Choosing winget
  mode accepts those programs' licence terms for the user.

## Command reference

```text
ferry-cli scan      [selection options] [--all] [--details] [--json <file>] [--no-winget]
ferry-cli capture   --out <file.ferry> [selection options] [password option] [--split auto|none|<size>] [--no-shadow]
ferry-cli restore   --in <file.ferry> [selection options] [restore options] [password option]
ferry-cli send      --to <new-pc>[:port] --code <code> [selection options]
ferry-cli receive   [--code <code>] [--timeout <minutes>] [--listen <ip>] [--port <n>] [selection options] [restore options]
ferry-cli undo      --list | <id> [--yes] | --discard <id>
ferry-cli info      <file.ferry> [password option] [--details]
ferry-cli plan      --out <plan.json> [--from-scan] [--no-winget]
ferry-cli version
ferry-cli help [<command>]
```

**Selection options:**
`--plan <file>`, `--include <rule>`, `--skip <rule>`, `--exclude <pattern>`, `--user <name>`, `--folder <path>`,
`--apps winget|copy`, `--no-winget` (don't ask winget which programs it can reinstall, which makes scans faster).

**Restore options:**
`--map OLD=NEW`, `--map-folder OLD=NEW`, `--conflicts keepboth|newer|overwrite|skip`, `--close-apps`,
`--no-restore-point`, `--dry-run`, `--sandbox <dir>`.

**Password options:** `--password [<pw>]`, `--password-env <VAR>`, `--password-file <file>`.

**Everywhere:** `--verbose`, `--quiet`. Options can repeat where it makes sense (`--map`, `--include`, `--exclude`
…), and `--name=value` works as well as `--name value`. `save` is another name for `capture`, and `apply` for
`restore`.

`--split` takes `auto` (split into 4 GB parts on FAT32 drives, the default), `none`, or a size such as `2GB` or
`700MB`.

## Limitations

* **Chromium browsers' saved passwords and cookies** (Chrome, Edge, Brave, Vivaldi, Opera) are encrypted to the old
  PC and can't be moved. Have users turn on browser sync, or export passwords, before the move.
* **Default apps** can't be set by another program on Windows 10/11. They're listed in "Still to do".
* **Copying programs** (`--apps copy`) is experimental. Programs with drivers, services tied to hardware, or
  licensing bound to the old PC may not work. Kernel drivers are never copied.
* **Printers** need a driver that's available on the new PC. Others are listed with their address.
* **OneDrive** cloud-only files are never downloaded, and OneDrive-synced folders are unticked by default because
  OneDrive brings them back.
* **Discovery** works only within one IPv4 subnet. Connect by name or address otherwise.
