using System.Runtime.CompilerServices;
using VRage.Audio;
using VRageMath;

namespace FactionsUI.Atc;

/// <summary>The emitter SE's raw voice asks for. GetSound reads only DebugData, SourceChannels,
/// CustomVolume and CustomMaxDistance for a 2D voice (MyXAudio2.cs:1890-1916); everything else is inert.</summary>
internal sealed class AtcEmitter : IMy3DSoundEmitter
{
    public MyCueId SoundId => default(MyCueId);
    public IMySourceVoice Sound => null;
    public IMySourceVoice SecondarySound => null;
    public Vector3D SourcePosition => Vector3D.Zero;
    public Vector3 Velocity => Vector3.Zero;
    public float DopplerScaler => 1f;
    public float? CustomMaxDistance => null;
    public float? CustomVolume => null;
    public bool Realistic => false;
    public bool Force3D => false;
    public bool Plays2D => true;
    public int SourceChannels { get; set; }
    public int LastPlayedWaveNumber { get; set; }
    public object DebugData { get; set; }
    public object SyncRoot { get; } = new object();
    public void SetSound(IMySourceVoice sound, [CallerMemberName] string caller = null) { }
}
