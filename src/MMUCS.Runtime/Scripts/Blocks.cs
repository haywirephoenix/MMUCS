using System.Collections.Generic;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

internal static class Blocks
{
    /// <summary><paramref name="b"/> and its descendants down to <paramref name="depth"/> levels, parents first.</summary>
    public static IEnumerable<ScummBlock> Walk(ScummBlock b, int depth)
    {
        yield return b;
        if (depth == 0) yield break;
        foreach (var c in b.Children)
            foreach (var x in Walk(c, depth - 1))
                yield return x;
    }
}
