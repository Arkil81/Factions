# Factions UI reference

Factions UI is the client plugin that shows you what Factions is doing. To install it, see
[Installing Factions UI](../install-client.md).

## Keys

| Key | What it does |
|---|---|
| **M** | Opens the Factions map. |
| **Alt** (hold) | Shows the names of scanner contacts on your HUD. |
| Market key (unset) | Opens the market window. |
| Expand key (unset) | Shows or hides the readout for creating a node where you are. |
| Expand commit key (unset) | Creates a node where you are, after asking you to confirm. |

The keys marked *unset* do nothing until you choose a key for them in the settings. All keys only
work while you are playing, not while you are typing in a text box.

## Settings

Open Pulsar's plugin list from the game's main menu (**Plugins**), select **Factions UI** and open
its settings. In game, **Ctrl+Alt+/** opens Pulsar's plugin settings.

| Section | Setting | Default | What it does |
|---|---|---|---|
| Scanner | (checkbox) | On | Draws scanner contacts on your HUD. |
| Map | Open the map | M | The key that opens the map. |
| Map | Station labels | On | Names on the map. |
| Map | Fleet routes | On | Lines showing where fleets are going. |
| Map | Empty nodes | Off | Also shows nodes with nothing built on them. |
| Map | Planet markers | On | Rings and names planets and moons. |
| Map | Exclusion sphere | Off | Shows the area that must be free of other nodes before you can create one where you are. |
| Market | Open the market | Unset | The key that opens the market window. |
| Expand | Placement readout | Unset | The Expand key: shows or hides the site readout. |
| Expand | Create node here | Unset | The Expand commit key: creates a node where you are, after asking you to confirm. |
| Radio | Voice volume | Full | The traffic-control voice's volume. |
| Diagnostics | Log diagnostics | Off | Writes extra detail to the game log. Leave it off unless asked. |
| Diagnostics | Rock collision on contact | Off | Saves memory by giving asteroids collision only near moving objects. |

The radio voice itself is switched on or off in the **Radio** window, not here.

## On your HUD

- **Scanner contacts**, coloured by owner. See [Finding your way](finding-your-way.md).
- **SIG**, bottom left: your ship's signature, which decides how far away others notice you.
- **The site readout**, top centre, while the Expand key has it switched on.
- **The status panel**, right edge, during a siege or occupation.

## Windows

| Window | How to open it | What it is for |
|---|---|---|
| Map | **M** | Every node and fleet you know about. Raid, conquer and defend campaigns. |
| Station window | **Open station** on the map or in the Assets tab | A station's details, and for your own stations its shipyard, development, trade, hold and stock. |
| Market | Market key, or **Market** in the Assets tab | Prices at stations you know, and trade routes. |
| Fleet | **Fleet** in the Assets tab | Your faction's fleet doctrine. |
| Store | **Store** in the Assets tab | Your faction's ship shop, and bank sales. |
| Campaigns | **Campaigns** in the Assets tab | Your faction's raid, conquer and defend campaigns. |
| Demand | **Demand** in the Assets tab | What your faction's stations need. |
| Diplomacy | **Diplomacy…** on the game's Factions tab, with an NPC faction selected | Your faction's diplomacy with that faction. |
| Radio | **Radio…** on the terminal's Comms tab | Switches the traffic-control voice on or off. |

## Additions to the game's own screens

- **Terminal (K):** an **Assets** tab and an **Events** tab.
- **Factions tab:** your stance with the selected NPC faction, and the **Diplomacy…** button.
- **Store blocks:** a **Ship inventory** choice to trade from your whole docked ship, and **Withdraw**
  and **Deposit** tabs at your own station.
- **Services Terminal:** **Refuel H2**, **Refuel O2** and **Rearm** for a docked ship; **Restore** in
  place of the repair price; and at your own station, **Send to Node** and **Pull** for stored ships.
- **Respawn screen:** your faction's founded stations as respawn options.
- **Antennas:** a **Radio squelch** slider in each antenna's terminal settings, which sets how faint
  a radio call can be before you stop hearing it.

## The radio voice

Station traffic control speaks to you over the radio: arrivals and departures, hostile fleets,
queues and holds, and the hails you receive when you enter a faction's space. It replaces the
written messages about convoys and traffic.

You hear it in your suit within about 10 km of the station, or from a ship's controls within about
30 km. Antennas extend that range.

## If it does not work

See *Checking that it works* and *Troubleshooting* in [Installing Factions UI](../install-client.md).
