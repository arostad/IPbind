# IPbind

<img src="icons/png/IPbind-256.png" width="180" align="left" alt="IPbind">

<br><br>

One-click static IP binder for air-gapped equipment. A compiled .NET WinForms tool that binds a set of static IPv4 addresses (no gateway, no DNS) onto one chosen LAN interface so you can reach gear that lives on different `/24` subnets, then revert that interface to DHCP with one click.

<br clear="all">

Current version: **2.2.102**.

## Build locally

Any Windows 10/11 box with .NET Framework (it is already installed). No Visual
Studio, SDK, or internet required.

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Exe.ps1
```

The build stamps `$Version` from `Build-Exe.ps1` into `Version.cs` (generated,
not committed) and produces `IPbind.exe` in this folder.

## CI

Every push, pull request, and manual `workflow_dispatch` builds `IPbind.exe` on
a Windows runner and uploads it as an Actions artifact named `IPbind`.
