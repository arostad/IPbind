# IPbind

<img src="icons/png/IPbind-256.png" width="180" align="left" alt="IPbind">

One-click static IP binder for air-gapped equipment. A compiled .NET WinForms tool that binds a set of static IPv4 addresses (no gateway, no DNS) onto one chosen LAN interface so you can reach gear that lives on different `/24` subnets, then revert that interface to DHCP with one click.

App created by [Andy Rostad](https://github.com/arostad).  Released under the [MIT License](https://github.com/arostad/IPbind/blob/main/LICENSE).

<br clear="all">

Current version: **2.3.112**.

## Download

**[Download Installer — IPbind-Setup.exe](https://github.com/arostad/IPbind/releases/download/latest/IPbind-Setup.exe)**

The Installer is recommended. It installs per-user under LocalAppData and does not require administrator access for installation. IPbind still elevates when run to apply network settings with `netsh`.

<sub>Prefer a single file? [Portable — IPbind.exe](https://github.com/arostad/IPbind/releases/download/latest/IPbind.exe)</sub>
