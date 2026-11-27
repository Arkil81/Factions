# Fleets and combat

## Your faction's own ships

A player faction that owns a station with a shipyard can build its own ships, which then fly for
the faction on their own: trading, mining, patrolling, defending and raiding.

### Fleet doctrine

What your shipyards build for your fleet is set by your faction's **fleet doctrine**: a list of ship
designs and how many of each you want. Nothing is built for the fleet until the list has at least
one line.

- Open the **Fleet** window from the terminal's **Assets** tab, or use `!factions fleet`.
- A leader adds a line with **New line…** (or `!factions fleet add (design) (count)`), and can change
  its priority, target count, or pause it.
- The count includes ships you already have, whether parked, flying or in production. It is a
  ceiling, not an order to build that many more.
- **Refits…** upgrades existing ships to a newer design, and **Decommission…** retires ships you no
  longer want.

### Ship designs

Your faction builds from a pool of designs. You can add your own ships to it:

- `!factions ship capture own (name)` captures the ship you are looking at as a design only your
  faction can build. `all` instead of `own` makes it available to everyone.
- `!factions station scan own (name)` does the same for the station you are standing in.

### The shipyard

The **Shipyard…** button in a station's window shows what is being built there, what parts are
missing, and lets a leader start a new build, pause, cancel or reorder builds. The same can be done
with `!factions station queue`.

### Selling ships

The **Store** window, opened from the **Assets** tab, sets up your faction's ship shop: which designs
you offer for sale and how many to keep in stock. Ships reserved for sale are not sent out with your
fleet.

## Campaigns: raiding, conquering and defending

A leader can send the faction's combat ships against another faction's node, or set them to defend
one of your own. Campaigns are placed on the map (client plugin only; there is no chat command):

1. Open the map with **M** and select a node.
2. On another faction's node, press **Raid** or **Conquer**. On one of your own, press **Defend**.
3. Confirm. Raiding or conquering is an act of war against the node's owner.

- **Raid** sends your combat ships to strike the node. Each strike is paid for from the faction's
  account.
- **Conquer** sends them to take the node.
- **Defend** sets them to patrol around your node.

You can have up to three campaigns at once, and each lasts six hours. You need at least one node of
your own to send ships from. You cannot target your own nodes, a node of a faction you have a pact
with, or a node you have not discovered.

The **Campaigns** window, opened from the **Assets** tab, lists your campaigns and lets you cancel
them. On the map they are marked `[RAID]`, `[CONQUEST]` and `[DEFEND]`.

## Attacking a station yourself

You do not need a fleet to attack a station. Players can besiege a Factions station directly.

### Besieging a station

1. **Break the shield.** Keep the station's safe zone under fire for five minutes. If you stop firing
   and the zone recovers for 30 seconds, the siege fails and you start over.
2. **Do the damage.** Once the safe zone is down and its turrets are live, either do enough damage to
   the station, or knock out its power. Stay within 15 km.
3. **Take it.** When the station's hold has fallen far enough and enough time has passed, you are
   offered the node in chat:

   > *You hold (node). Its owner cannot dislodge you. Take it? Reply "yes" to claim it, "no" to leave
   > it standing.*

   Reply `yes` to take it for your faction. Only a leader of your faction can answer. If you are not
   a leader, you are told so, and a leader of your faction who is online is offered the node
   instead. You need to be in a faction to take a node.

Firing on a station's safe zone costs you reputation with its owner, in proportion to the damage.
This applies to any station your faction does not own, whatever your standing with its owner, so
attacking a friendly station is costly.

You are kept informed with chat and on-screen messages as the siege progresses. With the client
plugin, a status panel on the right of the screen shows it instead.

### Occupying a site

A site with no station on it can be taken by staying there. If a member of your faction stays within
3 km of a node owned by a faction you are enemies with, its hold is stripped every five minutes.
Leaving the area ends the occupation.

Each time hold is stripped, the owner hails you and your reputation with it falls.

When an owner abandons a node under your attack, it becomes unclaimed, and you can take it by
accepting the claim offer that follows.

## When you are the target

NPC factions raid and try to conquer player stations too. Your faction's **Events** tab shows
attacks on your nodes, and the map shows *Under attack* and *Under siege* on the node. To defend:

- be there yourself, which also keeps your hold up,
- place a **Defend** campaign on the node,
- keep your fleet doctrine stocked with combat ships.

## Attacking other players

Attacking another player's grids while nobody in their faction is online is against the server
rules. If you damage a grid whose owner's whole faction has been offline for more than 15 minutes,
a red warning tells you so, and the attack is logged for the admins. Grinding counts. With the
client plugin, the scanner marks such a grid *owner offline* before you attack it.

Grids owned by an NPC are not covered, including the NPC members of a player faction: those can be
attacked at any time.

Logging off during a fight to protect your grids is also logged.

## In a fight

- After a fight near you, a message tells you the result: who attacked whom, whether the attack was
  repelled, and how many ships each side lost.
- When a ship is destroyed or disabled near you: *Kill confirmed: [TAG] (ship) disabled at (node).*
- Messages such as *[TAG] squadron closing to brawl* or *falling back to kite* tell you what an
  enemy squadron near you is doing.

## Crew

Some ships carry crew. You can give the crew of a ship you are in, or of the nearest crewed ship
within 400 m, orders by chat or from the seat's toolbar actions (listed as *Crew: …*):

| Order | What the crew does |
|---|---|
| `!factions crew mount` | Stay in their seats. |
| `!factions crew dismount` | Get out and form up behind the ship. Only works where there is ground to stand on. |
| `!factions crew ease` | Act on their own. |
| `!factions crew auto` | The default: stay seated in gravity, act on their own in space. |

If you move more than 400 m away from your crew, you are warned, and they are dismissed if you do not
come back within 20 seconds.

## Wing commands

Your faction's ships can be given orders from an antenna's toolbar actions: *Wing: form on
me*, *focus my target*, *hold*, *break off*, *return*, *release* and *protect me*. Like other toolbar
actions, they can also be triggered by timers, button panels or event controllers.
