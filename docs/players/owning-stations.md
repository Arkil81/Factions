# Owning stations

A player faction can own nodes, just as an NPC faction does: resource sites, outposts and full
stations. Owning nodes is what lets your faction trade by convoy, build its own ships, respawn at
its own stations and take part in the wider economy.

You need to be in a player faction to own anything. Create or join one in the game's own Factions
screen (**K**, then the **Factions** tab). Only a **leader** of the faction can spend its money or
change its settings; every member can see everything.

## Getting your first node

There are two ways.

### Accept a claim offer (free)

When you are at a site that nobody owns, Factions may offer it to your faction in chat:

> *Our scouts report an unclaimed (ore) site (N123). Claim it for [TAG]? Reply yes or no.*

Type `yes` in chat to take it, or `no` to leave it. Only a leader of your faction can answer. If no
leader is online, the member who receives the offer is told that a leader must answer it, and the
offer goes to the first leader who comes online. If you are not in a faction, you are told the site
is free and that you need a faction to claim it.

If your faction has more than one offer open, a plain `yes` answers none of them. You are told which
sites are on offer, and you answer one at a time by naming it, for example `yes N123` or `no N123`.

Sites become unclaimed when their owner loses them. An abandoned site stays claimable for several
hours before it disappears.

### Create a node where you stand (paid)

Your faction can also create a brand-new node at your current position, if the spot is far enough
from every other node. This costs 1,000,000 credits, paid from the faction's own account.

With the client plugin, set an **Expand** key and an **Expand commit** key in the plugin settings
(both are unset at first). Press the Expand key to show or hide a readout at the top of the screen:

- *SITE CLEAR* means a node can be created here, and shows the cost.
- *SITE OCCUPIED* means another node is too close. It does not say which one or where. Nodes in
  deep space must be very far apart, so you may need to fly a long way before a site is clear.

Press the Expand commit key to create the node. You are asked to confirm first.

By chat, `!factions expand` checks the spot and `!factions expand confirm` creates the node. Only a
leader can confirm.

The money comes from the faction's account, not your own. To put money into it, open the terminal
(**K**), go to the **Factions** tab, select your faction and use **Deposit**.

## Holding a node

Every node has a **hold** percentage, which shows how firmly your faction holds it. A node whose
hold falls to zero is lost.

- **Being there builds hold.** While a member of your faction is within about 5 km of a node you
  own, its hold rises. It takes about half an hour of presence to bring a node to full hold. Nearby
  nodes of yours benefit too, a little less for each step away.
- **Hold fades when nobody is there.** If no member visits for a long time, hold slowly falls.
- **You can pay to keep hold up.** In the station window, **Hold…** (or `!factions station hold`)
  sets the faction to pay automatically for hold on chosen nodes. **Budget** settings
  (`!factions station budget`) limit how much it spends.
- **Attacks reduce hold.** Another faction besieging or occupying your node strips its hold. See
  [Fleets and combat](fleets-and-combat.md).

## Founding a station

A claimed node starts as a bare site. **Founding** it builds a station there, with a safe zone,
stores and services. Founding costs 100,000 credits and gives you a choice of two styles,
**standard** or **fortified**. The style cannot be changed later.

Open the node's station window and press **Found…**, or use `!factions station found`.

Once founded, you can rename the station with `!factions station rename`.

## Building up a station

Stations grow by adding **capabilities**: a refinery, a manufactory, a shipyard, a fuel depot, a
sensor array that extends how far your station can see, and others. Each one costs credits plus real
materials, and some need another capability somewhere in your faction first.

Use **Develop…** in the station window, or `!factions station develop`. Installing a capability you
already have raises its level.

### The station window

Open it with **Open station** on the map or in the terminal's **Assets** tab. Its tabs are:

- **Overview:** owner, hold, safe zone, value, ore density, shipyard and workers.
- **Stock:** what the station holds.
- **Ships:** ships based there.
- **History:** what has happened there.
- **Demand:** what the station needs.

All tabs except Overview are only for nodes your faction owns. The buttons along the side open the
station's shipyard, its standing orders, development, rebuilding (**Refit…**), trade settings, hold
settings, and stock transfers.

### Moving goods in and out

- **Stock…** in the station window lets you withdraw goods from the station's stock, or deposit
  goods into it, while you are within 5 km.
- At your own station, the store block gains **Withdraw** and **Deposit** tabs that do the same from
  the store screen.

### Storing ships at a station

At your own station's Services Terminal, with the client plugin:

- **Send to Node** turns a docked ship into a stored ship at the node. Its cargo is added to the
  station's stock.
- Stored ships appear in **Stored Grids**, and **Pull** brings one back out.

### Mining and standing orders

Your faction's mining ships can work for you:

- `!factions station mine` sends a one-off mining run from a resource site to a station.
- `!factions station mine auto` sets up a recurring run.
- **Orders…** in the station window, or `!factions station orders`, sets standing orders: keep a
  given amount of an item in stock at a station, or mine on a schedule.

## Respawning at your own station

With the client plugin, every station your faction has founded appears as an option on the respawn
screen. Respawning there is free, but you can only do it once every five minutes, whichever station
you choose. It is not available while the station's safe zone is down. The respawn screen says why an
option is unavailable.

Without the client plugin, respawning at your own station is not possible. Use a medical room or
survival kit instead.

## Losing a node

A node is lost if its hold falls to zero, or if your faction runs out of money and cannot pay its
way. A lost node becomes unclaimed, and anyone can claim it for a few hours. Ships of yours based
there move to your nearest other node.

Another faction can also take a node from you by force. See
[Fleets and combat](fleets-and-combat.md).
