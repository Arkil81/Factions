# Finding your way

## The scanner

The scanner shows you the nodes and fleets that you have detected: stations, outposts, ore fields,
rare ore fields, ice fields, refineries, fuel depots, fleets and fights in progress.

**With the client plugin**, contacts are drawn directly on your HUD:

- Blue contacts belong to your own faction, white to neutral factions and red to enemies. Grey
  contacts are unclaimed or not yet identified.
- Hold **Alt** to show contact names.
- Contacts you are not detecting right now, but saw earlier, are drawn dimmed.
- Fleets show how many ships they have, and what they are doing, such as *Raid: (place)*.
- A station that is talking to you on the radio is marked with a pulsing ring, which is red if it
  is warning you off.

The scanner can be turned off in the plugin settings: see [Factions UI reference](factions-ui.md).

**Without the client plugin**, contacts appear as temporary GPS markers, and only while you are
seated at a cockpit or other ship controller. They are removed when you leave the seat. Unclaimed
sites are marked *(free)*.

### How far you can see

Your detection range comes from the antennas on your ship. A ship with no working antenna still
sees about 25 km. Each antenna adds range: a bigger antenna adds more, and so does setting it to a
larger broadcast radius. A large ship's antenna at full radius sees roughly 100 km. A player
on foot who is not in a faction sees stations and sites but not fleets.

Once you have detected a node, you remember it, so it stays on your map and scanner after you fly
away.

## The map

Press **M** to open the Factions map (client plugin only). The key can be changed in the plugin
settings. If you use the game's symmetry mode, note that it shares the M key; Factions leaves M
alone while you are placing blocks.

- Scroll to zoom, drag with the left mouse button to rotate, and drag with the right mouse button
  to move the view.
- Click a node or fleet to select it, and double-click to centre the view on it.
- The panel for a selected node shows its owner, and anything happening there: a siege, an attack,
  a raid that succeeded, a safe zone that is down.
- The panel for a selected fleet shows how many ships it has, where it is going and when it
  arrives.

From the selected node's panel you can:

- **Open station** to open its station window.
- **GPS** to add a GPS marker for it.
- **Raid**, **Conquer** or **Defend**, if you lead a faction: see
  [Fleets and combat](fleets-and-combat.md).

If the map says *No map uplink to this server*, the plugin is not connected to Factions on this
server. *Waiting for the world…* means it is connected and still receiving data.

## The terminal tabs

The client plugin adds two tabs to the terminal screen (**K**):

- **Assets** lists everything your faction owns: stations, convoys, ships and grids. From here you
  can open a station's window, and the Demand, Fleet, Store, Market and Campaigns windows.
- **Events** lists what has happened recently to your faction: attacks and losses, trades and
  convoys, industry and logistics. *Under attack now*, *Raided*, *Convoy plundered* and similar
  headlines are marked with an exclamation mark.

## How others detect you

NPC factions detect your ship with their own sensors, which reach out from their stations and
their ships. How far away they notice you depends on your ship's **signature**. Signature grows with
the ship's size and mass, with the weapons it carries, and with how full its cargo is. A big armed
hauler is seen from a long way off; a small empty scout is hard to spot.

A broadcasting antenna gives you away regardless: a ship whose antenna is broadcasting can be seen at
least as far away as its broadcast radius, just as in the normal game. Turn broadcasting off if you
do not want to be noticed.

The client plugin shows your current signature in the bottom-left corner of the screen, as **SIG**
followed by a number:

| Colour | Meaning |
|---|---|
| Green (below 0.5) | Quiet. Hard to detect. |
| Grey (below 1.5) | Ordinary. |
| Amber (below 4.0) | Loud. |
| Red (4.0 and above) | Lit up. Visible from a long way off. |

Without the plugin the readout is not shown, but your signature is still used to detect you.
