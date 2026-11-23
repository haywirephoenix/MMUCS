using System.Diagnostics;
using MMUCS.Core.Media;
using MMUCS.Core.Smush;
using MMUCS.Media.OpenAL;

namespace MMUCS.Benchmarks;

/// <summary>
/// The sound output on the real device: 5 seconds of the opening cutscene's sound (quietly), reading the
/// played position the way the window does. Not part of "all", since it plays sound.
/// </summary>
public static class Audio
{
    public static void Run()
    {
        var item = MediaLibrary.Scan(Host.GameFile, MediaKind.Cutscene, null).First(i => i.Id == "video/opening");
        var sound = SmushAudio.Decode(MediaLibrary.LoadVideo(item));
        var mixer = new Mixer(44100);
        using var output = new OpenALOutput(mixer);
        output.Start();
        mixer.Play(new PcmSource(sound.Pcm, sound.SampleRate, sound.Channels), MixGroup.Video, volume: 0.3f);

        int gcs = GC.CollectionCount(0);
        var sw = Stopwatch.StartNew();
        double worst = 0, total = 0;
        int reads = 0;
        long start = output.PlayedFrames;
        while (sw.Elapsed.TotalSeconds < 5)
        {
            long t = Stopwatch.GetTimestamp();
            _ = output.PlayedFrames;
            double ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            worst = Math.Max(worst, ms);
            total += ms;
            reads++;
            Thread.Sleep(2);
        }
        double played = (output.PlayedFrames - start) / (double)mixer.SampleRate;
        Console.WriteLine($"{output.Name}: position read {total / reads * 1000:0} µs on average, worst {worst:0.00} ms over {reads} reads; " +
                          $"played {played:0.000} s in {sw.Elapsed.TotalSeconds:0.000} s; underruns {output.Underruns}; gen0 collections {GC.CollectionCount(0) - gcs}");
    }
}
