# NPC factions and how they treat you

## What an NPC faction is

Each NPC faction runs its own small economy. It owns stations, outposts and resource sites, earns
money from production and trade, and spends it on ships, station upgrades, defence and war. It has a
personality: some factions are traders, some are miners, some are raiders who live by attacking
others. Each one keeps an opinion of every other faction, NPC and player alike.

You never need to follow any of this to play. What matters is how a faction treats *you*, and that
comes down to two things: your reputation with it, and whether you are inside its territory.

## Reputation

Your reputation with each NPC faction is the same number the game itself shows in the **Factions**
tab of the terminal. It falls into one of three bands, which decide how the faction treats you:

- **Friends.** You are welcome in its space, its stores trade with you, and its ships leave you
  alone.
- **Neutral.** You are tolerated. You can trade and travel, as long as you behave.
- **Enemies.** You are not welcome. Its stations refuse to trade with you, its traffic control tells
  you to leave, and its patrols will come for you if you stay.

New players start Neutral with most NPC factions. The Space Pirates (SPRT) and the Factorum (FCTM)
are hostile to newcomers from the start.

**If you are in a player faction**, the reputation the game shows you is your *faction's* standing
with each NPC faction, not your personal one. Everything your faction members do counts towards it.
If you leave the faction, your own record comes back.

### How reputation changes

- **Trading** with a faction's stations raises it slowly.
- **Lingering in its space while unwelcome** lowers it steadily.
- **Attacking** its stations or ships lowers it, in proportion to the damage you do.
- **Diplomacy**, if you lead a player faction, can change your faction's standing directly. See
  [Diplomacy](diplomacy.md).

## Entering a faction's space

NPC factions watch the space around their nodes. When one detects your ship for the first time, its
traffic control hails you. The hail comes from the nearest station's control and tells you how you
stand:

- *Welcome to (faction) space. You are among friends here.*
- *You have entered (faction) space. Mind your conduct and you will have no trouble from us.*
- *You have entered (faction) space. You are not welcome here. Turn back.*

You are hailed once per visit. Leaving and coming back within about five minutes counts as the same
visit. Members of a faction are never hailed by it, and player factions do not hail anyone.

A faction can only hail what it detects. A small, quiet ship may pass through unseen. See
[Finding your way](finding-your-way.md) for how detection works.

### If you are told to turn back

If you are in the Enemies band and stay, the faction escalates step by step:

1. Its ships start to shadow you.
2. You are warned: *You were told to leave (faction) space. Our patrol is cleared to engage.* From
   this point its ships may attack you.
3. Its ships pursue you.

While you remain in its space, moving slowly and not docked, your reputation with it keeps falling.
The faction may also send its station guards out to deal with you.

The simplest way out is to leave its space. The reputation loss only applies while you are moving
slowly and are not docked.

## NPC ships you will meet

Every NPC ship you see belongs to a fleet with a purpose. The fleet exists in the simulation all the
time, but it only becomes a real ship in the game world when a player is within about 15 km of it.

| Fleet | What it does | How to treat it |
|---|---|---|
| Trade convoy | Carries goods between stations. | Harmless. Attacking it hurts its owner's economy, and your reputation. |
| Patrol | Flies circuits around its faction's nodes. | Watches for intruders. Dangerous if you are its faction's enemy. |
| Guard | Defends a station that is under threat. | Attacks whatever it was sent to deal with. |
| Raid | Attacks another faction's station. | Dangerous to its target, and to anyone who gets in the way. |
| Conquest | Comes to take a station from its owner. | As a raid, but it stays until the station falls or it is beaten. |
| Retaliation | Strikes back at a faction that attacked its owner. | As a raid. |
| Gift | Carries a gift of goods from one faction to another. | Harmless. |

You are told about fleets near you. Within a few kilometres, a message such as *A [TAG] raiding
fleet has launched, bound for (place)!* tells you what is going on. With the client plugin, the
traffic-control radio voice announces arrivals, departures and hostile fleets instead, and the
scanner marks fleets on your HUD. What those markers mean is in
[Reading a fleet marker](finding-your-way.md#reading-a-fleet-marker).

### Why an NPC ship attacked you

Usually one of these:

- You are in the Enemies band with its faction, and you are in its space.
- You were hit by a raid or conquest fleet aimed at a station you were near, or at your own
  faction's station.
- Your faction, or you, attacked its faction, and it is retaliating.
- It is a pirate or other raider faction, which is hostile to everyone.

### Stations

NPC stations have safe zones, and many have turrets. A station's store and services are open to
anyone the owner is not hostile to. If the owner is hostile to you, or has an embargo against your
faction, you are refused with a message saying so.

Station stores can be looted, and they restock after about an hour. You are told when a station's
stores are stocked again.

Derelict wrecks left near a station are stripped and cleared by the owning faction's reclamation
crews from time to time.
