using System;
using VRage.Game.Entity;
using VRage.Game.ModAPI.Interfaces;
using VRage.Game.Utils;
using VRageMath;

namespace FactionsUI.Map;

/// <summary>
/// The map's camera. Installing this as <c>MySession.Static.CameraController</c> is what makes the
/// map window show the real world: the game keeps rendering every frame regardless of which screen
/// has focus, and it asks whichever controller is installed where to look from. So the planets,
/// atmospheres, clouds, sunlight and skybox on the map are the actual ones, not a drawing of them.
///
/// Orbit state is a focus point plus spherical offset. Distances are metres, matching SE.
/// </summary>
internal sealed class MapCameraController : IMyCameraController
{
    /// <summary>Pitch is clamped just short of the poles: at exactly +/-90 degrees the look
    /// direction becomes parallel to the up vector and the view matrix degenerates.</summary>
    private const double PitchLimit = Math.PI * 0.5 - 0.01;

    private const double MinDistanceM = 500.0;

    /// <summary>
    /// How far out the camera may pull, and so what the far plane has to cover. Sized from the
    /// world rather than picked round: the live graph's furthest node sits ~11 760 km from origin
    /// (2 538 nodes, measured 2026-08-22), so 50 000 km frames the whole system with room to spare.
    /// Keeping this as tight as the world allows is what protects depth precision -- an arbitrarily
    /// huge far plane is the likeliest way to break the atmosphere shader and z-fighting.
    /// </summary>
    public const double MaxDistanceM = 5.0e7;   // 50 000 km

    /// <summary>How long a reframe takes. Long enough to read as travel, short enough that a
    /// double-click still feels like a command rather than a cutscene.</summary>
    private const double GlideSeconds = 0.6;

    /// <summary>Ceiling on one frame's contribution to a glide, so a hitched frame advances it by
    /// a hitch's worth rather than by however long the frame took.</summary>
    private const double MaxFrameSeconds = 0.1;

    public Vector3D Focus;
    public double DistanceM = 50_000.0;
    public double Yaw;
    public double Pitch = 0.6;

    private bool _gliding;
    private Vector3D _glideFromFocus;
    private Vector3D _glideToFocus;
    private double _glideFromDistanceM;
    private double _glideToDistanceM;
    private double _glideElapsed;

    public Vector3D EyePosition
    {
        get
        {
            double cosPitch = Math.Cos(Pitch);
            var offset = new Vector3D(
                Math.Cos(Yaw) * cosPitch,
                Math.Sin(Pitch),
                Math.Sin(Yaw) * cosPitch);
            return Focus + offset * DistanceM;
        }
    }

    /// <summary>Orbit by a screen-space drag. Scaled in radians per pixel, so the same drag turns
    /// the same amount whatever the zoom.</summary>
    public void Orbit(double deltaYaw, double deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = MathHelperD.Clamp(Pitch + deltaPitch, -PitchLimit, PitchLimit);
    }

    /// <summary>
    /// Pan the focus across the view plane. The step scales with distance so a drag moves the world
    /// under the cursor by roughly the same screen amount at every zoom level -- panning at system
    /// scale with a step tuned for close-up would be unusable, and vice versa.
    /// </summary>
    public void Pan(double rightPixels, double upPixels, double pixelsPerRadian)
    {
        CancelGlide();
        MatrixD orientation = MatrixD.CreateLookAt(EyePosition, Focus, Vector3D.Up);
        Vector3D right = new Vector3D(orientation.M11, orientation.M21, orientation.M31);
        Vector3D up = new Vector3D(orientation.M12, orientation.M22, orientation.M32);

        double metresPerPixel = DistanceM / Math.Max(pixelsPerRadian, 1.0);
        Focus += right * (-rightPixels * metresPerPixel) + up * (upPixels * metresPerPixel);
    }

    /// <summary>Exponential zoom: each notch is a fixed ratio, so the same wheel gesture feels the
    /// same whether the camera is 10 km or 10 million km out.</summary>
    public void Zoom(int notches)
    {
        if (notches == 0) return;
        CancelGlide();
        DistanceM = MathHelperD.Clamp(
            DistanceM * Math.Pow(0.85, notches), MinDistanceM, MaxDistanceM);
    }

    /// <summary>Frame a point at a sensible standoff, in one step. Used when the map opens on the
    /// player, where there is no previous view to travel from.</summary>
    public void FrameOn(Vector3D target, double distanceM)
    {
        CancelGlide();
        Focus = target;
        DistanceM = MathHelperD.Clamp(distanceM, MinDistanceM, MaxDistanceM);
    }

    /// <summary>
    /// Frame a point over <see cref="GlideSeconds"/> instead of in one step, so the player keeps
    /// their bearings: a map that cuts from a system view to a station gives no clue where that
    /// station was. Yaw and pitch are left alone -- the travel is the camera closing in, not the
    /// view spinning as well.
    /// </summary>
    public void GlideTo(Vector3D target, double distanceM)
    {
        _glideFromFocus = Focus;
        _glideToFocus = target;
        _glideFromDistanceM = DistanceM;
        _glideToDistanceM = MathHelperD.Clamp(distanceM, MinDistanceM, MaxDistanceM);
        _glideElapsed = 0.0;
        _gliding = true;
    }

    public void CancelGlide()
    {
        _gliding = false;
    }

    /// <summary>
    /// Advance an in-flight glide. Called once per frame by the screen, with the frame's own
    /// elapsed time clamped: a frame stalled by a world load must not teleport the camera most of
    /// the way to its target in one step.
    ///
    /// <para>The focus moves linearly and the distance <b>geometrically</b>, matching
    /// <see cref="Zoom"/>: distances here span five orders of magnitude, and a linear ramp from
    /// 11 000 km to 150 km spends nearly the whole travel inside the last few per cent of the
    /// journey, which reads as a stall followed by a lurch.</para>
    /// </summary>
    public void Advance(double seconds)
    {
        if (!_gliding) return;
        _glideElapsed += MathHelperD.Clamp(seconds, 0.0, MaxFrameSeconds);
        double t = MathHelperD.Clamp(_glideElapsed / GlideSeconds, 0.0, 1.0);
        double k = t * t * (3.0 - 2.0 * t);   // smoothstep: no jerk at either end

        Focus = Vector3D.Lerp(_glideFromFocus, _glideToFocus, k);
        DistanceM = _glideFromDistanceM * Math.Pow(_glideToDistanceM / _glideFromDistanceM, k);

        if (t < 1.0) return;
        Focus = _glideToFocus;
        DistanceM = _glideToDistanceM;
        _gliding = false;
    }

    // ---- IMyCameraController ------------------------------------------------------------------

    public void ControlCamera(MyCamera currentCamera)
    {
        Vector3D eye = EyePosition;
        // Up is world-up except when looking almost straight down it, where LookAt degenerates.
        // Pitch is clamped short of the poles precisely so this stays well-conditioned.
        // smooth:false -- SE would otherwise ease towards each new matrix, fighting the
        // orbit input every frame and leaving the camera permanently lagging the cursor.
        currentCamera.SetViewMatrix(MatrixD.CreateLookAt(eye, Focus, Vector3D.Up), smooth: false);
    }

    // The map camera is not attached to an entity and takes no gameplay input: the screen drives it
    // directly from HandleInput, so every member below is deliberately inert.
    public bool IsInFirstPersonView { get; set; }
    public bool ForceFirstPersonCamera { get; set; }
    public bool EnableFirstPersonView { get; set; }
    public bool AllowCubeBuilding => false;
    public MyEntity Entity => null;

    public void Rotate(Vector2 rotationIndicator, float rollIndicator) { }
    public void RotateStopped() { }
    public void OnAssumeControl(IMyCameraController previousCameraController) { }
    public void OnReleaseControl(IMyCameraController newCameraController) { }
    public bool HandleUse() => false;
    public bool HandlePickUp() => false;
    public MatrixD? GetOverridingFocusMatrix() => null;
}
