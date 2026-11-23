using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SkiaSharp;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// Images made together (a room's background and object images, one set of z-plane masks, one live
/// costume frame), freed together as soon as nothing uses them. Each Skia image holds its pixels outside
/// the managed heap, where the garbage collector can't see them, so leaving them to finalizers lets them
/// pile up as rooms change.
/// <para>The creator holds the first reference and calls <see cref="Release"/> once nothing it shows
/// uses the images; every draw operation showing them holds one more until the renderer has finished.</para>
/// </summary>
public sealed class ImageSet
{
    private readonly List<SKImage> _images = [];
    private int _refs = 1;

    /// <summary>Adds an image to the set (from any thread) and returns it; null stays null.</summary>
    public SKImage Add(SKImage image)
    {
        if (image != null)
        {
            lock (_images) _images.Add(image);
        }
        return image;
    }

    public void Retain()
    {
        int refs = Interlocked.Increment(ref _refs);
        Debug.Assert(refs > 1, "an image set was retained after it was freed");
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) != 0)
        {
            return;
        }

        lock (_images)
        {
            foreach (var image in _images) image.Dispose();
            _images.Clear();
        }
    }
}
