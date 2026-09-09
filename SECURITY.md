# Security

## What the program does at the OS level

Beast Clicker synthesises mouse input. The complete list of what it touches:

* `SendInput` to inject mouse button events into the system input queue
* `SetCursorPos` when clicking at a fixed point
* `RegisterHotKey` for global hotkeys, so it can start and stop while another window
  has focus
* `GetCursorPos` for the pick location feature
* a settings file at `%APPDATA%\BeastClicker\config.json`

It does not hook the keyboard, does not record what you type, does not inject into other
processes, does not load a driver, and makes no network connections at all. Every call
above lives in [`ClickEngine.cs`](src/BeastClicker/Engine/ClickEngine.cs) and
[`HotkeyManager.cs`](src/BeastClicker/Input/HotkeyManager.cs), both short enough to read
in a sitting.

## Antivirus false positives

Anything that synthesises input looks like automation malware to a heuristic scanner, and
an unsigned binary scores worse again. A false positive is plausible. If you would rather
not trust a binary, the source builds with one `dotnet build`.

## The MSIX

The published `.msix` is signed with a self signed certificate. Installing it means adding
that certificate to Trusted People, and trusting a certificate means trusting anything
signed with it, now and later. The portable `.exe` needs none of that, which is why the
README recommends it.

## Reporting something

Open an issue. For anything you would rather not post publicly, use GitHub's
[private vulnerability reporting](https://github.com/beastops/beastclicker/security/advisories/new).

## Out of scope

This is an input automation utility. It makes no attempt to evade anti cheat or detection
systems, and requests to add that are out of scope.
