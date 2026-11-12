# IPbind for Mac (preview)

A rough Apple Silicon prototype of IPbind. Not a release.

## How it binds addresses

Your adapter's own service stays on DHCP. IPbind adds the static addresses next to it:

- **Persistent** (default): one extra manual network service per address on the same hardware port, named `IPbind <device> <ip>`. macOS keeps them through restarts and replugs. They have no router, so they never take the default route.
- **Temporary**: `ifconfig` aliases. Gone after unplug, link change, sleep or restart.

**Gateway support** (Persistent only) puts a router and up to two DNS servers on the one chosen address. New services sit last in the service order, so Wi-Fi or DHCP still wins the default route when they have one.

**Return to DHCP** deletes the IPbind services on that adapter and removes the aliases. If the adapter's own service was in Manual mode, it's switched back to DHCP.

Each Apply or Return to DHCP is a single `do shell script … with administrator privileges` call, so macOS shows its normal password prompt. No helper is installed.

## Build

On a Mac with Xcode 15 or later: `mac/build-app.sh`. The `Mac prototype` workflow builds it on `macos-14` for pushes to the `mac-prototype` branch.
