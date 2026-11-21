# Chat commands

Every command is typed in chat and starts with `!factions`. Most of them act on your **faction**, so
you need to be in one. Commands marked **leader** can only be used by a leader or founder of your
faction; any member can use the others.

Where a command takes a `nodeId` (such as `N123`) and you leave it out, it uses the node nearest to
you, within 5 km. If there is none, you are asked to stand closer or name the node.

Most commands print help when given `help`, for example `!factions station queue help`.

## Getting a node

| Command | Who | What it does |
|---|---|---|
| `!factions expand` | any member | Checks whether a new node can be created where you are, and what it costs. |
| `!factions expand confirm` | leader | Creates the node, paid from the faction's account. |
| `yes` / `no`, or `yes <site>` / `no <site>` | a leader of the faction offered it | Answers a claim offer or a conquer offer in chat. With more than one offer open, name the site, for example `yes N123`. |

## Your stations

| Command | Who | What it does |
|---|---|---|
| `!factions station status` | any member | Lists your faction's nodes with their hold. |
| `!factions station status (nodeId)` | any member | Details of one node: hold, capabilities, top stock. |
| `!factions station rename [nodeId] (name)` | any member | Renames a founded station (up to 40 characters). |
| `!factions station found [nodeId] [standard\|fortified]` | leader | Builds a station on a claimed node. The style cannot be changed later. |
| `!factions station develop [nodeId] (capability)` | leader | Adds a capability, or raises its level. |
| `!factions station develop (capability) reserve` | leader | Lets production set components aside for that capability. |
| `!factions station develop (capability) priority (n)` | leader | How that capability ranks against ship builds for parts. |
| `!factions station hold [nodeId] [rank\|off]` | leader | Pays automatically to keep a node's hold up. With nothing after it, lists your nodes. |
| `!factions station budget [income\|treasury\|floor] [value\|default]` | leader | Limits what the faction spends on upkeep. |
| `!factions station refit [nodeId] (design)` | leader | Rebuilds a founded station as a different design. |
| `!factions station refit apply [nodeId]` | leader | Swaps in a finished rebuild now, instead of on your next arrival. |
| `!factions station scan (own\|all) (name)` | any member | Captures the station you are in as a design. `own` keeps it for your faction; `all` shares it with everyone. |
| `!factions station prefabs [nodeId]` | any member | Lists station designs. |

## Shipyard

| Command | Who | What it does |
|---|---|---|
| `!factions station queue [nodeId]` | any member | Lists builds in the shipyard. |
| `!factions station queue prefabs (role)` | any member | Lists ship designs for a role: `Trade`, `Raid`, `Patrol` or `Mine`. |
| `!factions station queue build …` | leader | Starts a build. See `!factions station queue help`. |
| `!factions station queue cancel\|pause\|resume\|priority …` | leader | Manages builds. |
| `!factions ship capture (own\|all) (name)` | any member | Captures the ship you are looking at as a design. |

## Mining and standing orders

| Command | Who | What it does |
|---|---|---|
| `!factions station mine (site) [station] [Iron:50,Nickel:50]` | leader | A one-off mining run. |
| `!factions station mine auto [station] [period\|off] [ores]` | leader | A recurring mining run. With nothing after it, lists them. |
| `!factions station orders` | any member | Lists standing orders. |
| `!factions station orders stock (nodeId\|all) (item) (amount)` | leader | Keeps an amount of an item in stock, for example `Ingot:Iron`. |
| `!factions station orders mine (station) (seconds) [ores]` | leader | Mines on a schedule. |
| `!factions station orders remove\|pause\|resume\|priority (id) …` | leader | Manages orders (ids such as `SO-001`). |

## Trade

| Command | Who | What it does |
|---|---|---|
| `!factions station trade` | any member | Shows your faction's convoy trade setting. |
| `!factions station trade [buy\|sell\|both\|off] [on\|off]` | leader | Turns convoy trade with NPC factions on or off. |
| `!factions station tradebudget …` | leader | Sets how much the faction may spend on trade. |
| `!factions station banksell` | any member | Shows what the central bank may buy from you. |
| `!factions station banksell [sell\|target\|keep] [item\|all] [n]` | leader | Sets it: surplus only, above a target, or nothing. |
| `!factions store` | any member | Shows your ship shop. |
| `!factions store add (design) (count)` | leader | Offers ships of a design for sale. |
| `!factions store remove\|target\|pause\|resume (id) …` | leader | Manages the shop's lines (ids such as `SH-001`). |
| `!factions store off\|on` | leader | Closes or opens the shop. |

## Fleet

| Command | Who | What it does |
|---|---|---|
| `!factions fleet` | any member | Shows your fleet doctrine. |
| `!factions fleet add (design) (count)` | leader | Adds a line: how many of a design the fleet should have. |
| `!factions fleet remove\|priority\|target\|concurrent (id) …` | leader | Manages lines (ids such as `FD-001`). |
| `!factions fleet pause\|resume (id)` | leader | Pauses or resumes a line. |
| `!factions fleet refit [design count]` | leader | Upgrades ships to a newer design. With nothing after it, lists what can be refitted. |
| `!factions fleet decommission (design) [count] [confirm]` | leader | Retires ships. Without `confirm` it only reports what would happen. |

Raid, conquer and defend campaigns have no chat command; they are placed on the map. See
[Fleets and combat](fleets-and-combat.md).

## Diplomacy

| Command | Who | What it does |
|---|---|---|
| `!factions diplomacy status [tag]` | any member | Your standing with NPC factions. |
| `!factions diplomacy war (tag)` | leader | Declares war. |
| `!factions diplomacy peace (tag)` | leader | Offers peace. |
| `!factions diplomacy embargo (tag)` | leader | Stops trade with them. |
| `!factions diplomacy lift (tag)` | leader | Ends an embargo. |
| `!factions diplomacy gift (tag)` | leader | Sends them a gift of credits. |
| `!factions diplomacy propose (tag) nonaggression\|alliance` | leader | Proposes a pact. |
| `!factions diplomacy withdraw (tag)` | leader | Withdraws your offer. |

See [Diplomacy](diplomacy.md) for what each one does.

## Crew

| Command | Who | What it does |
|---|---|---|
| `!factions crew mount` | the ship's faction | Crew stay seated. |
| `!factions crew dismount` | the ship's faction | Crew get out and form up behind the ship. |
| `!factions crew ease` | the ship's faction | Crew act on their own. |
| `!factions crew auto` | the ship's faction | Seated in gravity, on their own in space. The default. |

These act on the crewed ship you are in, or the nearest one within 400 m.

## Kits

| Command | Who | What it does |
|---|---|---|
| `!factions kit` | anyone | Lists the kits you can claim, what each one holds, and when you can next claim it. |
| `!factions kit (name)` | anyone | Claims a kit, for example `!factions kit starter`. |

You do not need a faction for these. Each kit has its own wait between claims, and the wait applies
across every server in the cluster. If your inventory is full, the rest of the kit floats beside you.

## Common replies

| Reply | What it means |
|---|---|
| *You are not in a faction.* | Join or create a faction first. |
| *[TAG] is not registered with Factions yet — claim a node first.* | Your faction needs its first node. See [Owning stations](owning-stations.md). |
| *No node found within 5 km — stand closer, or specify a nodeId explicitly.* | Move closer to the node, or add its id to the command. |
| *Only a leader of [TAG] can spend its treasury or components…* | The command needs a leader of your faction. |
