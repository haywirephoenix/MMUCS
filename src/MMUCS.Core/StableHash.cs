using System;

namespace MMUCS.Core;

/// <summary>
/// Deterministic 64-bit FNV-1a hash for on-disk cache keys. Unlike HashCode.Combine (randomised
/// per process), these keys are identical across runs, so disk caches actually hit.
/// </summary>
public static class StableHash
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static long Of(params long[] values)
    {
        ulong h = Offset;
        foreach (long v in values)
        {
            ulong x = unchecked((ulong)v);
            for (int i = 0; i < 8; i++)
            {
                h ^= (byte)(x >> (i * 8));
                h *= Prime;
            }
        }
        return unchecked((long)h);
    }

    public static long Of(string s)
    {
        ulong h = Offset;
        foreach (char c in s ?? string.Empty)
        {
            h ^= (byte)c; h *= Prime;
            h ^= (byte)(c >> 8); h *= Prime;
        }
        return unchecked((long)h);
    }
}
