# Diplomacy

A player faction has a diplomatic stance towards each NPC faction, just as NPC factions do towards
each other. Every member can see it; only a **leader** can change it.

Your faction's stance does not drift on its own. It changes only when your leader acts, or when a
truce ends.

## Where to find it

With the client plugin, open the terminal (**K**), go to the game's own **Factions** tab and select
an NPC faction. Below the usual details you see how that faction regards yours, and a
**Diplomacy…** button that opens the diplomacy window. Its buttons are greyed out for members who
are not leaders.

By chat, `!factions diplomacy status` shows your standing with every NPC faction, and
`!factions diplomacy status (tag)` shows one in detail.

The game's own **Declare war** and **Send peace** buttons also work. They go through the same rules
as below, and only a leader can use them.

## What a leader can do

| Action | Chat command | What it does |
|---|---|---|
| Declare war | `!factions diplomacy war (tag)` | Your faction is at war with theirs. Trade stops in both directions. Ending the war needs their agreement. |
| Embargo | `!factions diplomacy embargo (tag)` | Stops convoy trade in both directions, without going to war. |
| Lift embargo | `!factions diplomacy lift (tag)` | Ends an embargo. |
| Send a gift | `!factions diplomacy gift (tag)` | Sends 10,000 credits from your faction's account. It improves how they regard your faction. Not accepted by factions that refuse all dealings, and only once every half hour per faction. |
| Offer peace | `!factions diplomacy peace (tag)` | Asks to end a war. They accept if they are tired of the war, or are already offering peace themselves. Peace begins with a truce of about 20 minutes. |
| Propose non-aggression | `!factions diplomacy propose (tag) nonaggression` | A pact not to attack each other. They accept if they are offering peace or are tired of fighting. |
| Propose alliance | `!factions diplomacy propose (tag) alliance` | An alliance. It is only agreed if they propose one to you as well. |
| Withdraw an offer | `!factions diplomacy withdraw (tag)` | Takes back an offer you made. |

Offers stay open for 24 hours. Some factions refuse every offer and every gift.

## What it means in practice

- **At war**, the other faction's stations refuse to trade with your members, and its fleets may
  raid your stations.
- **Under an embargo**, no convoys trade between your factions, and its stations refuse your members.
- **In a pact or alliance**, your faction cannot raid or conquer its nodes.

While you are in a player faction, the reputation the game shows you with each NPC faction is your
faction's standing with it. See [NPC factions and how they treat you](npc-factions.md).
