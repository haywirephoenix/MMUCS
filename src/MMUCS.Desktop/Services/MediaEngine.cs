using System;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Media;
using MMUCS.Media.OpenAL;

namespace MMUCS.Desktop.Services;

/// <summary>
/// The app's sound: one <see cref="Mixer"/> and the output playing it, OpenAL Soft on the default device,
/// or a silent stand-in that keeps time when no device opens. The media player owns one while it's open;
/// the game runner will own one the same way.
/// </summary>
public sealed class MediaEngine : IDisposable
{
    public const int SampleRate = 44100;

    public MediaEngine()
    {
        Mixer = new Mixer(SampleRate);
        try
        {
            Output = new OpenALOutput(Mixer);
        }
        catch (Exception ex)
        {
            Log.Warn($"Sound output unavailable, playing silently: {ex.Message}");
            Output = new NullAudioOutput(Mixer, ex.Message);
        }
        Output.Start();
    }

    public Mixer Mixer { get; }
    public IAudioOutput Output { get; }
    public bool HasSound => Output is not NullAudioOutput;

    public void Dispose()
    {
        Mixer.StopAll();
        Output.Dispose();
    }
}
