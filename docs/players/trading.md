# Trading

## Station stores

The store blocks at Factions stations work like the game's own stores, with two differences.

**Prices follow supply.** Each station keeps a real stock of goods. An item the station is short of
costs more to buy and earns more to sell; an item it has plenty of is cheap and earns little. Prices
range from half the base price to three times it. When you buy or sell, the station's stock really
changes, so selling a large load of one item drives its price down.

**Owners can refuse you.** A station whose owner is hostile to you, or whose faction has an embargo
against yours, will not sell to you, buy from you, or provide services. You get a message such as
*(Owner) is hostile to (you) — trade refused* or *(Owner) has embargoed (you) — trade refused*.
Improving your reputation, or making peace, reopens the store. See
[NPC factions and how they treat you](npc-factions.md) and [Diplomacy](diplomacy.md).

Trading with a faction slowly improves your reputation with it.

### Selling a whole ship's cargo

With the client plugin, a store's inventory dropdown has a **Ship inventory** entry. Choose it to
sell from, or buy into, every cargo container on your docked ship at once, rather than one container
at a time.

## Station services

The Services Terminal at a Factions station offers repair, salvage and the other usual services.
What you pay goes to the station's owner.

With the client plugin, the **Active Grids** tab also offers:

- **Refuel H2** and **Refuel O2**, filling your ship's tanks from the station's own stock.
- **Rearm**, refilling your ship's ammunition from the station's stock.

Your ship must be locked to the station with a connector. You are shown a price first and asked to
confirm, and you are never charged more than the price you were shown.

At a Factions station, the Repair tab's price panel is replaced by a **Restore** panel.

## The market window

The market window (client plugin) lists the prices that stations you know about are offering. Open
it from the terminal's **Assets** tab with the **Market** button, or set a key for it in the plugin
settings.

- The price list shows the item, the station, whether the station is buying or selling, the price,
  the quantity and how old the price is.
- **Show opportunities** lists trade routes instead: where to buy an item, where to sell it, the
  profit per unit and the distance between the two.

Prices are what you last learned about each station, so distant stations' prices can be out of date.

## Contracts

Contract blocks at Factions stations offer four kinds of contract that come from the faction's real
needs:

- **Acquisition:** bring an item to this station. The station asks for what it is shortest of.
- **Hauling:** take goods from this station to another of the faction's stations.
- **Repair:** repair the station. A station offers at most one at a time.
- **Bounty:** destroy a hostile fleet that is actually on its way to attack the station.

Completing these contracts helps the faction: the goods arrive, the station is repaired, the raid is
stopped. Other contract types work as in the normal game.

## Trading as a faction

A player faction that owns stations can trade automatically with the NPC factions, by convoy, the
same way NPC factions trade with each other. Its leader turns this on:

- **Trade** in the station window, or `!factions station trade`, chooses whether the faction buys,
  sells or both. Turning buying on also lets neighbouring NPC factions know what your stations need,
  so their convoys bring it.
- `!factions station tradebudget` sets how much the faction may spend.
- **Bank sales**, from the Store window, or `!factions station banksell`, controls what the central
  bank may buy from your stations: surplus only (the default), only what is above a target you set,
  or nothing.
- The faction can also sell ships it builds through its **Store** shelf. See
  [Fleets and combat](fleets-and-combat.md).

See [Chat commands](chat-commands.md) for the exact command forms.
