using System.Collections.Generic;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;


public static class ScummStatics
{
    public static readonly Dictionary<long, int> RoomOffsets = new(); // roomNo → file offset
    // private ScummBlock _rnamBlock;
    public static List<string> RoomNames = new();
    public static Dictionary<int, string> RoomLookup = new(); // fast reverse lookup


}