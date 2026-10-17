# Changelog

## 1.0.0 (2026-10-17)

First release.

**Download:** `Ferry-1.0.0-win-x64.zip`. Unzip it anywhere (or onto a USB stick) and run `Ferry.exe`. There's
nothing to install, and no .NET runtime is needed. 64-bit Windows 10 or 11.

### What's in it

* **Two ways to move:** directly between PCs over the network, protected by a one-time pairing code and
  encryption, or through a migration file on a USB drive, external disk or network share. Files can be
  password-protected (AES-256) and split into 4 GB parts for FAT32 drives.
* **What moves:** user files and folders (into the new user's folders, wherever they are), extra folders on any
  drive, program settings, Windows settings (Wi-Fi with passwords, printers, mapped drives, wallpaper and theme,
  input and accessibility settings, taskbar pins, fonts, environment variables), browser profiles (Chrome, Edge,
  Brave, Vivaldi, Opera, Firefox, Thunderbird) and Outlook and Office data (signatures, `.pst` files, templates,
  dictionaries, Sticky Notes).
* **Programs** are reinstalled fresh with winget by default. Copying a program's files and registry entries is
  available as an experimental option.
* **Paths are rewritten** for the new account in shortcuts, settings files and registry values.
* **Undo:** every change on the new PC is recorded and can be reversed. Dry run and sandbox modes for trying first.
* **Reports:** an HTML report and a JSON copy after every run, with a "Still to do" list for the user.
* **For IT:** `ferry-cli.exe` for silent runs, plan files, user mapping (for example domain to Microsoft Entra ID),
  documented exit codes. See `docs\it-deployment-guide.md`.
* **Guides:** `docs\user-guide.md` (step by step) and `docs\it-deployment-guide.md`.

### Known limitations

* Saved passwords and cookies in Chromium browsers (Chrome, Edge, Brave, Vivaldi, Opera) only work on the PC that
  saved them, so they aren't moved. Turn on browser sync first.
* Windows doesn't let programs set default apps; they're listed for the user to set.
* Copying programs is experimental. Programs with drivers or hardware-bound licences may need reinstalling.
* Network discovery works within one subnet; connect by name or address across subnets.
* The executables aren't code-signed yet, so Windows SmartScreen may warn the first time they run.
