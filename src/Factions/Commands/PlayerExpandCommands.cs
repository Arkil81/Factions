using System;
using Sandbox.Game.World;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using Factions.Core;

namespace Factions.Commands
{
    /// <summary>
    /// The chat half of a player faction creating a node where there is none — the player's own
    /// Expand (FAC-197; docs/economy-territory.md → "Expanding — creating a node where there is
    /// none"). Acts on where the caller is standing, so it needs neither a node id nor the client
    /// plugin: it is the graceful-degradation path the client plugin's map bubbles and in-flight
    /// readout sit on top of, and it prints the same verdict they do.
    ///
    /// <para><b>Two verbs, not one.</b> A bare call quotes; only <c>confirm</c> charges. The price is
    /// the largest single purchase a player makes and the act is irreversible, so a mistyped node id
    /// on a neighbouring command must not be able to spend it.</para>
    /// </summary>
    public class PlayerExpandCommands : CommandModule
    {
        [Command("factions expand", "Create a node for your faction where you are standing: !factions expand [confirm]")]
        [Permission(MyPromoteLevel.None)]
        public void Expand(string confirm = null)
        {
            MyFaction seFaction;
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerSeFaction(Context, out seFaction, out owner, out error))
            {
                Context.Respond(error);
                return;
            }

            Vec3 posKm;
            if (!PlayerCommandHelpers.TryResolvePositionKm(Context.Player, out posKm, out error))
            {
                Context.Respond(error);
                return;
            }

            FactionRuntime runtime = FactionsPlugin.Instance.Runtime;
            long playerIdentityId = Context.Player.IdentityId;

            bool commit = !string.IsNullOrEmpty(confirm)
                          && string.Equals(confirm, "confirm", StringComparison.OrdinalIgnoreCase);
            if (!commit)
            {
                if (!string.IsNullOrEmpty(confirm))
                {
                    Context.Respond("Usage: !factions expand (quote) or !factions expand confirm (create).");
                    return;
                }
                PlayerExpandQuote quote = runtime.QuoteExpandForPlayer(
                    owner, seFaction.Tag, playerIdentityId, seFaction.FactionId, posKm);
                Context.Respond(quote == null ? "Factions runtime not ready." : DescribeQuote(seFaction.Tag, quote));
                return;
            }

            // Keyed on the SE faction, not the roster: a faction owning nothing has no FactionState yet.
            string refusal = PlayerAuthority.SpendRefusal(playerIdentityId, seFaction.Tag);
            if (refusal != null) { Context.Respond(refusal); return; }

            PlayerExpandResult result = runtime.ExpandForPlayer(
                owner, seFaction.Tag, playerIdentityId, seFaction.FactionId, posKm);
            Context.Respond(result == null ? "Factions runtime not ready." : DescribeResult(seFaction.Tag, result));
        }

        /// <summary>The verdict at the caller's own position. Occupied says only that — never by
        /// whom and never where, which is the one place this surface deliberately stops short of what
        /// the server knows (see the design's "Occupied says only 'occupied'").</summary>
        private static string DescribeQuote(string tag, PlayerExpandQuote quote)
        {
            NodePlacementProbe p = quote.Probe;
            string where = "Zone " + p.Zone + ", " + p.SeparationKm.ToString("0.#")
                           + " km separation in force here.";

            if (!p.Clear)
                return "OCCUPIED. " + where + " Fly further out and try again.";

            string landing = p.SnapDistanceKm >= 1.0
                ? " The node would settle " + p.SnapDistanceKm.ToString("0.#") + " km from you, at "
                  + Coords(p.PlacedKm) + "."
                : " The node would be created right here.";

            if (quote.Shortfall > 0)
                return "CLEAR. " + where + landing + " " + HowToFund(tag, quote);

            return "CLEAR. " + where + landing
                   + " Cost " + quote.CostCredits.ToString("N0") + " credits from [" + tag + "]'s faction account"
                   + ". Say '!factions expand confirm' to create it.";
        }

        private static string DescribeResult(string tag, PlayerExpandResult result)
        {
            switch (result.Refusal)
            {
                case PlayerExpandRefusal.Occupied:
                    return "OCCUPIED — nothing was charged. "
                           + result.Quote.Probe.SeparationKm.ToString("0.#")
                           + " km separation is in force here. Fly further out and try again.";
                case PlayerExpandRefusal.ChargeRefused:
                    return "Not enough in the faction account — nothing was charged or created. "
                           + HowToFund(tag, result.Quote);
                case PlayerExpandRefusal.EconomyUnavailable:
                    return "The economy isn't available right now — nothing was charged or created.";
                case PlayerExpandRefusal.NoFaction:
                    return "You are not in a faction.";
            }

            SpatialNode node = result.Node;
            return "[" + tag + "] now holds " + node.Id + " (" + node.Zone + ") at " + Coords(node.Position)
                   + " — " + result.Quote.CostCredits.ToString("N0") + " credits from the faction account"
                   + ". Stay near it to build hold; '!factions station status' tracks it from here.";
        }

        /// <summary>What the faction account holds against the cost, and how a member tops it up: the
        /// vanilla faction terminal's Deposit, which reaches the ledger like any other deposit.</summary>
        private static string HowToFund(string tag, PlayerExpandQuote quote)
        {
            string held = quote.WalletBalance >= 0
                ? "[" + tag + "]'s faction account holds " + quote.WalletBalance.ToString("N0") + " credits; "
                : "";
            long need = quote.Shortfall > 0 ? quote.Shortfall : quote.CostCredits;
            return held + "an Expand costs " + quote.CostCredits.ToString("N0") + ". Deposit at least "
                   + need.ToString("N0") + " credits into the faction account: K (terminal) > Factions > "
                   + "[" + tag + "] > Deposit.";
        }

        /// <summary>Km-scale world position printed in game metres, which is what a GPS marker and
        /// every other in-game coordinate readout use.</summary>
        private static string Coords(Vec3 posKm)
        {
            return "GPS:" + (posKm.X * 1000.0).ToString("0")
                   + ":" + (posKm.Y * 1000.0).ToString("0")
                   + ":" + (posKm.Z * 1000.0).ToString("0");
        }
    }
}
