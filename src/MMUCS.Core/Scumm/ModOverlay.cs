using System;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;

/// <summary>
/// Mod images drawn over the game's own (<c>"base": "game"</c> in an object image's .meta.json or a costume cel's
/// meta.json entry): the game's image from the player's copy first, then the mod's PNG over it, so a mod holds only the
/// pixels its author drew and none of the game's. A pixel of alpha exactly 1 (<see cref="Erase"/>) erases the game's
/// pixel under it. <see cref="Strip"/> turns a mod PNG that holds the whole picture into one of those.
/// </summary>
public static class ModOverlay
{
    /// <summary>An overlay PNG's pixel that erases the game's under it: alpha 1 of 255, any colour (written black).</summary>
    public const uint Erase = 0x01000000;

    /// <summary>Whether an ARGB pixel of an overlay PNG erases the game's under it.</summary>
    public static bool Erases(uint argb) => argb >> 24 == 1;

    /// <summary>
    /// The erasing pixels of an overlay PNG's <paramref name="argb"/>, which are then made see-through in it (so they're
    /// read as nothing else); null when it has none.
    /// </summary>
    public static bool[] TakeErased(uint[] argb)
    {
        bool[] erased = null;
        for (int i = 0; i < argb.Length; i++)
            if (Erases(argb[i]))
            {
                (erased ??= new bool[argb.Length])[i] = true;
                argb[i] = 0;
            }
        return erased;
    }

    /// <summary>
    /// <paramref name="mod"/> at <paramref name="modX"/>,<paramref name="modY"/> drawn over <paramref name="game"/> at
    /// <paramref name="gameX"/>,<paramref name="gameY"/> (both in palette indices, see-through where they're
    /// <paramref name="transparent"/>, the room's or the costume's), the game's pixels under <paramref name="erased"/>
    /// (the mod's, or null) taken away: a surface over both, with its top-left.
    /// </summary>
    public static (IndexedSurface Surface, int X, int Y) Over(IndexedSurface game, int gameX, int gameY,
        IndexedSurface mod, int modX, int modY, byte transparent, bool[] erased = null)
    {
        int left = Math.Min(gameX, modX), top = Math.Min(gameY, modY);
        int right = Math.Max(gameX + game.Width, modX + mod.Width), bottom = Math.Max(gameY + game.Height, modY + mod.Height);
        int w = right - left, h = bottom - top;
        var pixels = new byte[w * h];
        Array.Fill(pixels, transparent);
        for (int y = 0; y < game.Height; y++)
            for (int x = 0; x < game.Width; x++)
            {
                byte v = game.Pixels[y * game.Pitch + x];
                if (v != transparent) pixels[(gameY - top + y) * w + gameX - left + x] = v;
            }
        for (int y = 0; y < mod.Height; y++)
            for (int x = 0; x < mod.Width; x++)
            {
                byte v = mod.Pixels[y * mod.Pitch + x];
                int at = (modY - top + y) * w + modX - left + x;
                if (v != transparent) pixels[at] = v;
                else if (erased != null && erased[y * mod.Width + x]) pixels[at] = transparent;
            }
        return (new IndexedSurface { Width = w, Height = h, Pitch = w, Pixels = pixels, TransparentIndex = transparent }, left, top);
    }

    /// <summary>
    /// What <see cref="Strip"/> made of a mod PNG: the overlay's ARGB pixels and size, where its top-left is from the
    /// PNG's (0 or less: it's widened to take in the game's image), how many of the PNG's pixels it kept, how many
    /// it cleared as the game's, and how many of the game's pixels it erases.
    /// </summary>
    public sealed record Stripped(uint[] Argb, int Width, int Height, int ShiftX, int ShiftY, int Kept, int Cleared, int Erased);

    /// <summary>
    /// The mod PNG <paramref name="argb"/> (<paramref name="modded"/>: it as the game draws it now, in palette indices,
    /// in place of the game's) as an overlay on <paramref name="game"/>, whose top-left is at
    /// <paramref name="gameX"/>,<paramref name="gameY"/> from the PNG's (see-through where it's
    /// <paramref name="gameTransparent"/>): each pixel that's the game's index there is made see-through, each of the
    /// game's pixels the PNG leaves see-through or doesn't reach is marked <see cref="Erase"/>, and the rest are kept as
    /// drawn. Drawn over the game's, it shows what the PNG did.
    /// </summary>
    public static Stripped Strip(uint[] argb, IndexedSurface modded, IndexedSurface game, int gameX, int gameY, byte gameTransparent)
    {
        byte modSee = modded.TransparentIndex, gameSee = gameTransparent;
        // Wide enough for the game's image too, so all the game's pixels it hides can be erased.
        int left = Math.Min(0, gameX), top = Math.Min(0, gameY);
        int w = Math.Max(modded.Width, gameX + game.Width) - left, h = Math.Max(modded.Height, gameY + game.Height) - top;
        var result = new uint[w * h];
        int kept = 0, cleared = 0, erased = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int mx = x + left, my = y + top, gx = mx - gameX, gy = my - gameY;
                bool onMod = mx >= 0 && my >= 0 && mx < modded.Width && my < modded.Height;
                bool onGame = gx >= 0 && gy >= 0 && gx < game.Width && gy < game.Height;
                byte v = onMod ? modded.Pixels[my * modded.Pitch + mx] : modSee;
                byte under = onGame ? game.Pixels[gy * game.Pitch + gx] : gameSee;
                uint c = onMod ? argb[my * modded.Width + mx] : 0;
                int at = y * w + x;
                if (v == modSee)
                {
                    if (under != gameSee) { result[at] = Erase; erased++; }
                }
                else if (under != gameSee && v == under) cleared++;
                else
                {
                    // A faint pixel kept mustn't read as an erasing one.
                    result[at] = Erases(c) ? (c & 0xFFFFFF) | 0x02000000 : c;
                    kept++;
                }
            }
        return new Stripped(result, w, h, left, top, kept, cleared, erased);
    }

    /// <summary>
    /// How many pixels differ between two pictures in palette indices, each at its own top-left (so a mod drawn two ways
    /// can be checked to draw the same), see-through where they're <paramref name="transparent"/> and beyond their edges.
    /// </summary>
    public static int Differences(IndexedSurface a, int ax, int ay, IndexedSurface b, int bx, int by, byte transparent)
    {
        int left = Math.Min(ax, bx), top = Math.Min(ay, by);
        int right = Math.Max(ax + a.Width, bx + b.Width), bottom = Math.Max(ay + a.Height, by + b.Height);
        static byte At(IndexedSurface s, int x, int y, byte see) =>
            x >= 0 && y >= 0 && x < s.Width && y < s.Height ? s.Pixels[y * s.Pitch + x] : see;
        int n = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (At(a, x - ax, y - ay, transparent) != At(b, x - bx, y - by, transparent)) n++;
        return n;
    }

    /// <summary>ARGB pixels as the RGBA bytes <see cref="PngWriter.Write"/> takes.</summary>
    public static byte[] ToRgba(uint[] argb)
    {
        var rgba = new byte[argb.Length * 4];
        for (int i = 0; i < argb.Length; i++)
        {
            uint c = argb[i];
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = ((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));
        }
        return rgba;
    }
}
