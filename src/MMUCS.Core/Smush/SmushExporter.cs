using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Smush;

/// <summary>What exporting one video produced.</summary>
public sealed record SmushExportSummary(string Name, int Frames, int ImagesWritten, int Problems, TimeSpan Elapsed)
{
    /// <summary>Decoder checks: block-coded frames that left data unread, the most left, and copies from outside the screen.</summary>
    public int FramesWithUnreadBytes { get; init; }
    public int MaxUnreadBytes { get; init; }
    public int OutsideCopies { get; init; }
}

/// <summary>
/// Unpacks a cutscene into editable files, following the asset IDs in docs/bundle-format.md:
/// <code>
/// out/video/opening/video.json         size, frame rate, sample rate, and which image each frame shows
/// out/video/opening/frame/0000.png     8-bit indexed PNG (palette indices kept), one per distinct picture
/// out/video/opening/audio.wav          the cutscene's sound (16-bit stereo, 22,050 Hz), starting with frame 0
/// </code>
/// A frame that shows the same picture as the one before (a held shot, FTCH) gets no file of its own:
/// <c>frames</c> in video.json maps every frame number to the frame whose image it shows. A palette
/// change (a fade) counts as a new picture.
/// </summary>
public static class SmushExporter
{
    public static async Task<SmushExportSummary> ExportAsync(string sanPath, string outputFolder,
        Action<int> frameDone = null, CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();
        var video = SmushVideo.Load(sanPath);
        string dir = Path.Combine(outputFolder, "video", video.Name);
        string frameDir = Path.Combine(dir, "frame");
        Directory.CreateDirectory(frameDir);

        int count = video.Frames.Count;
        string format = "D" + Math.Max(4, (count - 1).ToString().Length);
        var shows = new int[count];
        byte[] lastPixels = null, lastPalette = null;
        int lastImage = -1, written = 0, audio = 0, text = 0;

        var writes = new List<Task>();
        using var gate = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount - 1));
        var decoder = new SmushDecoder(video);

        try
        {
        foreach (var frame in decoder.Frames(token))
        {
            audio += frame.AudioChunks;
            text += frame.TextChunks;

            bool same = lastPixels != null
                && frame.Pixels.AsSpan().SequenceEqual(lastPixels)
                && frame.Palette.AsSpan().SequenceEqual(lastPalette);
            if (!same)
            {
                lastPixels = [.. frame.Pixels];
                lastPalette = [.. frame.Palette];
                lastImage = frame.Index;
                string file = Path.Combine(frameDir, frame.Index.ToString(format) + ".png");
                byte[] pixels = lastPixels, palette = lastPalette;

                await gate.WaitAsync(token);
                writes.Add(Task.Run(() =>
                {
                    try
                    {
                        using var fs = File.Create(file);
                        PngWriter.WriteIndexed(fs, video.Width, video.Height, pixels, palette);
                    }
                    finally { gate.Release(); }
                }, token));
                written++;
            }
            shows[frame.Index] = lastImage;
            frameDone?.Invoke(frame.Index);
        }
        }
        finally
        {
            // Let started writes finish before the gate goes away, cancelled or not.
            try { await Task.WhenAll(writes); }
            catch (OperationCanceledException) { }
        }

        var sound = SmushAudio.Decode(video);
        if (sound != null)
        {
            using var wav = File.Create(Path.Combine(dir, "audio.wav"));
            Audio.WavWriter.Write(wav, sound.Pcm, sound.Channels, sound.SampleRate, SmushAudioTrack.Bits);
            written++;
        }
        WriteManifest(Path.Combine(dir, "video.json"), video, shows, format, audio, text, sound);
        return new SmushExportSummary(video.Name, count, written, decoder.Problems, sw.Elapsed)
        {
            FramesWithUnreadBytes = decoder.FramesWithUnreadBytes,
            MaxUnreadBytes = decoder.MaxUnreadBytes,
            OutsideCopies = decoder.OutsideCopies,
        };
    }

    private static void WriteManifest(string path, SmushVideo video, int[] shows, string format, int audio, int text, SmushAudioTrack sound)
    {
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        var h = video.Header;
        w.WriteStartObject();
        w.WriteString("id", "video/" + video.Name);
        w.WriteString("source", Path.GetFileName(video.Path));
        w.WriteNumber("width", video.Width);
        w.WriteNumber("height", video.Height);
        w.WriteNumber("frameRate", h.FrameRate);
        w.WriteNumber("frameCount", video.Frames.Count);
        if (h.FrameCount != video.Frames.Count) w.WriteNumber("headerFrameCount", h.FrameCount);
        w.WriteNumber("sampleRate", h.SampleRate);
        w.WriteString("frameFiles", $"frame/{{frame:{format}}}.png");
        w.WriteNumber("audioChunks", audio);
        if (sound != null)
        {
            w.WriteStartObject("audio");
            w.WriteString("file", "audio.wav");
            w.WriteNumber("sampleRate", sound.SampleRate);
            w.WriteNumber("channels", sound.Channels);
            w.WriteNumber("bits", SmushAudioTrack.Bits);
            w.WriteNumber("frames", sound.FrameCount);
            w.WriteString("starts", "with frame 0");
            w.WriteEndObject();
        }
        w.WriteNumber("textChunks", text);
        // One number per frame: the frame whose PNG it shows. Kept on one line.
        w.WritePropertyName("frames");
        var sb = new StringBuilder("[");
        for (int i = 0; i < shows.Length; i++) sb.Append(i == 0 ? "" : ",").Append(shows[i]);
        w.WriteRawValue(sb.Append(']').ToString(), skipInputValidation: true);
        w.WriteEndObject();
    }
}
