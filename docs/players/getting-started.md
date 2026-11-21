# Getting started

## What Factions is

On an ordinary Space Engineers server, the NPC factions are mostly scenery. Their stations sit in
place, their traders follow fixed patterns, and nothing they do depends on what anyone else does.

Factions replaces that with a living economy. Every NPC faction has a treasury, a set of stations
and outposts it owns, and an opinion of every other faction, including yours. NPC factions mine,
refine and manufacture. They trade with each other by sending real convoys between their stations.
They build ships, defend their territory, raid their rivals and sometimes go to war. All of this
keeps running whether or not any player is nearby.

Most of the time this activity is simulated in the background. When you come close to it, it
becomes real: a convoy you meet in space is an actual ship you can talk to, trade with, escort or
attack, and what happens to it changes the simulation. A faction that loses its convoys runs short
of goods and money. A station that is besieged can change hands.

Player factions take part in the same economy. Your faction can own stations, trade with the NPC
factions, build its own fleet, make peace or war, and take territory.

### A few words you will see often

- **Faction tag.** Every faction has a short tag in square brackets, such as `[ABC]`. Messages from
  Factions name factions by their tag.
- **Node.** A place that a faction can own: a station, an outpost, or a resource site such as an
  ore field or ice field. Each node has an id such as `N123`.
- **Station.** A node with a built station on it. Stations have stores, services and a safe zone.
- **Hold.** How firmly a faction holds a node, shown as a percentage. A node whose hold drops to
  zero is lost.
- **Leader.** The leader or founder of a faction, as set in the game's own Factions screen. Only
  leaders can spend a faction's money or change its settings. Every member can look at everything.

## When you first arrive

If you start in a respawn pod or rover, Factions sets you down near a station that is willing to
trade with newcomers, or near an online member of your faction if you have one. Raider factions are
never chosen as your first neighbour.

You also receive two things:

- A **GPS marker** named *Nearest trading station: (station name) [TAG]*. It stays on your HUD until
  you next respawn this way.
- A **datapad** in your inventory, which names the same station and its position.

Fly to that station first. It will sell you what you need, buy what you have, and its owners will
not shoot at you.

Respawning at a medical room, survival kit or in your suit works exactly as in the normal game.

## Install the client plugin

Much of what Factions does is only visible with the **Factions UI** client plugin: the scanner
display, the map, the station windows, the traffic-control radio voice and respawning at your own
stations. Without it you still get the economy and the chat messages, but you are working half
blind.

Installing it takes a few minutes: see [Installing Factions UI](../install-client.md).

Once it is installed and you join the server, it connects by itself. If the server and the plugin
are on different versions, a red message on screen tells you so. Restarting the game lets the
plugin update itself.

## Your first hour

1. **Get to the trading station** your GPS marker points at. Sell what you mined on the way, and buy
   what you need. Prices depend on how much of each item the station has: goods it is short of
   cost more, and it pays more for them.
2. **Look around with the map.** Press **M** to open the Factions map. It shows the stations and
   sites you know about, who owns them, and the convoys you have detected. See
   [Finding your way](finding-your-way.md).
3. **Read the messages you receive.** When you fly into a faction's space, its traffic control hails
   you. The hail tells you how that faction regards you: welcome, tolerated, or not welcome. If you
   are told to turn back, do so, or expect to be intercepted. See
   [NPC factions and how they treat you](npc-factions.md).
4. **Take contracts.** Contract blocks at Factions stations offer work that the faction actually
   needs: delivering goods it is short of, hauling cargo between its stations, repairing a station,
   or defending against a raid that is really on its way. See [Trading](trading.md).
5. **Make or join a faction.** Owning stations, building fleets and diplomacy all happen at the level
   of a player faction. Create one or join one with the game's own Factions screen in the terminal
   (**K**, then the **Factions** tab).
6. **Get your first node.** If you fly through an unclaimed site with your faction, you may be
   offered it for free in chat. Reply `yes` to take it. You can also pay to create a new node where
   you stand. See [Owning stations](owning-stations.md).

## Messages you will receive

Factions talks to players in chat. Some of these messages are addressed to you alone; others go to
everyone near an event. The common ones:

| Message | What it means |
|---|---|
| *You have entered (faction) space…* | A faction's traffic control has detected you. The rest of the message tells you whether you are welcome. |
| *A [TAG] raiding fleet has launched, bound for (place)!* | A real NPC fleet near you is on its way somewhere. Raids attack; convoys trade. |
| *Our scouts report an unclaimed site… Claim it for [TAG]? Reply yes or no.* | You can take this node for your faction for free. Reply `yes` or `no` in chat. |
| *(Owner) will not trade with you* or *…trade refused* | That station's owner is hostile to you or has an embargo against you. Its store and services are closed to you. |
| *(Station): stores were raided. They restock in N minutes.* | Someone has emptied that station's loot recently. |
| *Kill confirmed…* or *…was repelled. Hulls lost…* | The result of a fight near you. |

With the client plugin, many of these are also spoken by the traffic-control radio voice, and
messages about convoys and station traffic are spoken instead of written.
