using System.Globalization;
using Factions.Wire;
using Sandbox.Graphics;
using VRage.Game.Components;
using VRage.Utils;
using VRageMath;

namespace FactionsUI;

/// <summary>
/// Shows the player how loud their own ship currently is.
///
/// <para><b>Why this belongs on the HUD at all.</b> Every other fog surface answers "what can I
/// see"; this is the only one that answers "what can see me". The server already computes the number
/// — it is the same value a faction's sensors detect the player by — and without it the emission
/// model is invisible: a player has no way to tell that firing a weapon or charging a jump just
/// tripled the range at which they are found.</para>
///
/// <para><b>One number, deliberately.</b> The breakdown is not sent. What is adding to it is legible
/// from what the ship is doing — burning, broadcasting, shooting, charging — and putting the
/// per-source weights on the wire would publish the whole emission model to anyone reading packets,
/// for a readout the player can infer by flying.</para>
///
/// <para>Zero before the first message, and after the link drops, which is also what an unlinked or
/// ungranted client keeps — the field simply does not draw.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class SignatureComponent : MySessionComponentBase
{
    public static SignatureComponent Instance { get; private set; }

    /// <summary>The server's last word on this player's signature. Dimensionless: 1.0 is the model's
    /// reference hull.</summary>
    public static double Signature { get; private set; }

    private const float Scale = 0.65f;

    /// <summary>Bottom-left of the client area, clear of the vanilla toolbar and the scanner's own
    /// contact labels, which are drawn around their contacts rather than at a fixed anchor.
    ///
    /// <para>Normalised to the client area, so the horizontal offset is a fraction rather than a
    /// pixel count: 0.104 is ~200 px at 1920 wide and scales with the display instead of drifting on
    /// one. Moved right twice on operator feedback, 2026-08-28 — 0.012 to 0.116 to 0.220, ~400 px in
    /// total, because the original sat under other HUD furniture. Two hand-tuned nudges to a
    /// hardcoded constant are the argument for FAC-142, not a settled position.</para></summary>
    private static readonly Vector2 Anchor = new Vector2(0.220f, 0.965f);

    public override void LoadData()
    {
        Instance = this;
        Signature = 0;
        UplinkComponent.RegisterHandler(WireMessageKind.SignatureStatus, OnStatus);
    }

    protected override void UnloadData()
    {
        Instance = null;
        Signature = 0;
    }

    private static void OnStatus(byte[] data)
    {
        double value;
        if (!FactionsWire.TryReadSignatureStatus(data, out value)) return;
        // A non-positive value would mean the server could not resolve the grid; keep the last good
        // reading rather than flashing a zero the player cannot act on.
        if (value > 0) Signature = value;
    }

    /// <summary>Called from the same HUD draw pass the scanner markers use — see
    /// <see cref="Patches.HudMarkerPatch"/>. Sprites queued outside that pass land in a different
    /// batch and flicker when other UI composites over them.</summary>
    public void Draw()
    {
        if (Signature <= 0) return;

        MyGuiManager.DrawString("Blue",
            "SIG " + Signature.ToString("0.00", CultureInfo.InvariantCulture),
            Anchor, Scale, Colour(Signature),
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_BOTTOM,
            useFullClientArea: true, maxTextWidth: float.PositiveInfinity, ignoreBounds: true);
    }

    /// <summary>Quiet reads calm, loud reads urgent. Banded rather than a gradient so the colour is a
    /// glanceable state and not a second number to interpret; the figure beside it carries the
    /// precision. Thresholds sit either side of the model's 1.0 reference hull.</summary>
    private static Color Colour(double signature)
    {
        if (signature < 0.5) return new Color(120, 200, 120);   // quiet
        if (signature < 1.5) return new Color(210, 210, 210);   // ordinary
        if (signature < 4.0) return new Color(235, 190, 90);    // loud
        return new Color(235, 110, 90);                          // lit up
    }
}
