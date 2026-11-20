using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace FactionsUI;

/// <summary>
/// World-space to screen-space projection, shared by every FactionsUI overlay.
///
/// The coordinate-space rules here were paid for with live bugs and are not negotiable: the result
/// is normalized across the FULL screen, so anything drawn from it must go through
/// <c>MyGuiManager</c> with <c>useFullClientArea: true</c>. Mapping these coordinates through the
/// narrower safe-GUI rectangle instead makes markers point short of their target, worsening away
/// from screen centre.
/// </summary>
internal static class Projection
{
    /// <summary>Vanilla's projection (see MyHudMarkerRender): transform to view space, then clip
    /// space, flipping when the point is behind the camera so the direction stays meaningful.</summary>
    public static bool Project(
        Vector3D world, ref MatrixD view, ref MatrixD projection, out Vector2 normalized, out bool behind)
    {
        normalized = Vector2.Zero;
        Vector3D viewPos = Vector3D.Transform(world, view);
        behind = viewPos.Z > 0.0;

        Vector4D clip = Vector4D.Transform(viewPos, projection);
        if (behind)
        {
            clip.X *= -1.0;
            clip.Y *= -1.0;
        }
        if (clip.W == 0.0) return false;

        normalized = new Vector2(
            (float)(clip.X / clip.W / 2.0) + 0.5f,
            (float)((0.0 - clip.Y) / clip.W / 2.0) + 0.5f);
        return true;
    }

    /// <summary>
    /// The world-space ray under the cursor, for picking things that have no marker to hit-test
    /// against. <paramref name="screenPx"/> is the mouse in window pixels --
    /// <c>MyInput.Static.GetMousePosition()</c> -- which is the space
    /// <c>WorldLineFromScreen</c> takes and the one vanilla's own cursor picking uses
    /// (<c>MyEntityTransformationSystem.CreateRayFromCursorPosition</c>). This is a different
    /// space from <see cref="Project"/>'s normalized output and the two must not be mixed.
    /// </summary>
    public static bool TryCursorRay(Vector2 screenPx, out Vector3D from, out Vector3D direction)
    {
        from = Vector3D.Zero;
        direction = Vector3D.Forward;
        IMyCamera camera = MyAPIGateway.Session?.Camera;
        if (camera == null) return false;
        LineD ray = camera.WorldLineFromScreen(screenPx);
        if (ray.Length <= 0.0) return false;
        from = ray.From;
        direction = ray.Direction;
        return true;
    }

    /// <summary>
    /// The live game camera. The map window works through this too rather than through its own
    /// matrices: it drives <c>MySession.Static.CameraController</c>, so while the map is open the
    /// game camera IS the map camera, and the world renders behind the screen for free.
    /// </summary>
    public static bool TryGetCamera(out MatrixD view, out MatrixD projection, out Vector3D position)
    {
        view = MatrixD.Identity;
        projection = MatrixD.Identity;
        position = Vector3D.Zero;
        IMyCamera camera = MyAPIGateway.Session?.Camera;
        if (camera == null) return false;
        view = camera.ViewMatrix;
        projection = camera.ProjectionMatrix;
        position = camera.Position;
        return true;
    }
}
