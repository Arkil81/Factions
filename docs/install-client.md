# Installing Factions UI

Factions UI is the client plugin for servers running Factions. It draws the ship scanner, map,
market, node and fleet windows, and the traffic-control voice. On a server without Factions it does
nothing.

It installs through [Pulsar](https://github.com/SpaceGT/Pulsar). Pulsar builds it from this
repository's source and keeps it up to date, so there is no file to download.

## Before you start

- **Pulsar** installed and loading with Space Engineers:
  [latest Pulsar release](https://github.com/SpaceGT/Pulsar/releases/latest).
- **Space Engineers started with `-sources`.** Without this launch option Pulsar hides its
  **Sources** button, and plugins from outside the Pulsar plugin list cannot be added. In Steam:
  right-click Space Engineers → **Properties** → **General** → **Launch Options**, and add
  `-sources`. Keep any options already there, separated by a space.

## Install

1. Start Space Engineers and click **Plugins** in the main menu.
2. Click **Sources**. The first time, Pulsar shows an **External Sources Warning**: plugins can run
   any code, so only add sources you trust. Click **Acknowledge**.
3. The Sources window has three columns: **Hubs**, **Plugins** and **Mods**. The Hubs and Plugins
   columns each have a button with the same link icon. Use the one under **Plugins**, whose tooltip
   reads **Add Remote Plugin**. It opens a dialog titled **Remote Plugin Source** with four fields.
   If the dialog asks for a **Display Name** and has no **Metadata File**, it is the hub dialog:
   cancel it and use the button under Plugins.

   The quickest way to fill the dialog: copy this line to the clipboard *before* clicking the
   button, and Pulsar fills in all four fields itself:

   ```
   seplugin/Arkil81/Factions/main/FactionsUI.xml
   ```

   Or type them in:

   | Field | Value |
   |---|---|
   | GitHub User | `Arkil81` |
   | Repo Name | `Factions` |
   | Branch Name | `main` |
   | Metadata File | `FactionsUI.xml` |

   **Metadata File** is filled with `PluginHub.xml` by default; replace it. Click **Add**, then
   **Apply**.
4. Back in the plugin list, find **Factions UI**, tick **Enabled**, and click **Apply**.
5. Pulsar asks to restart the game. Accept. Pulsar downloads and compiles the plugin during start-up,
   so this start takes longer than usual.

The plugin is now loaded. It connects by itself when you join a server running Factions.

## Updates

Pulsar checks this repository at game start, at most once every two hours, and builds the newest
release on its own. You do not reinstall anything.

Client and server must speak the same wire protocol, and each
[release](https://github.com/Arkil81/Factions/releases) states which one it uses. Releases are timed
to match the servers' updates. If a server updates first, restart the game to pick up the new
client.

## Checking that it works

The Space Engineers log is the newest `SpaceEngineers_*.log` in `%AppData%\SpaceEngineers`. Search
it for `FactionsUI:`.

| Log line | Meaning |
|---|---|
| `FactionsUI: initialised` | The plugin loaded |
| `FactionsUI: ATC voice library: <n> clips in …` | The traffic-control voices loaded |
| `FactionsUI: UPLINK_GRANTED …` | Connected to the server's Factions plugin |
| `FactionsUI: UPLINK_REFUSED protocol client=<a> server=<b>` | Client and server versions differ; see below |
| `FactionsUI: UPLINK_REQUEST sent (attempt 5/5)` with no `UPLINK_GRANTED` after it | The server is not running Factions, or did not answer |

## Troubleshooting

- **No Sources button.** The game was started without `-sources`; check the launch options and
  restart.
- **Factions UI is not in the plugin list.** Check the four fields in the Sources window, in
  particular **Metadata File**, which must read `FactionsUI.xml`. Then click **Refresh** in the
  plugin list.
- **`UPLINK_REFUSED protocol`.** The client and server are on different releases. Restart the game
  so Pulsar fetches the current release. If the server is the older one, the client links again once
  the server updates.
- **Factions UI added as a hub.** Adding the repository under **Hubs** instead of **Plugins** also
  works, but Pulsar then downloads the whole repository each time it changes, instead of one small
  file. Remove the hub entry and add it under **Plugins** instead.

## Uninstall

Untick **Enabled** for Factions UI in the plugin list and restart. To remove it completely, also
delete the Factions entry under **Sources** → **Plugins**.
