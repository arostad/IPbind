# IPbind

<img src="icons/png/IPbind-256.png" width="180" align="left" alt="IPbind">

One-click static IP binder for air-gapped equipment. A compiled .NET WinForms tool that binds a set of static IPv4 addresses (no gateway, no DNS) onto one chosen LAN interface so you can reach gear that lives on different `/24` subnets, then revert that interface to DHCP with one click.

App created by [Andy Rostad](https://github.com/arostad).  Released under the [MIT License](https://github.com/arostad/IPbind/blob/main/LICENSE).

<br clear="all">

Current version: **2.3.110**.

## Download

- [**Portable — IPbind.exe**](https://github.com/arostad/IPbind/releases/download/latest/IPbind.exe) (v2.3.110)
- [**Installer — IPbind-Setup.exe**](https://github.com/arostad/IPbind/releases/download/latest/IPbind-Setup.exe) (v2.3.110)

Portable is the primary, single-file download. The matching Installer installs per-user under LocalAppData and does not require administrator access for installation. Both are refreshed together on each main release; IPbind still elevates when run to apply network settings with `netsh`.
