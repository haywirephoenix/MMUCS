using System.Collections.Generic;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;


public static class ScummStatics
{
    public static readonly Dictionary<long, int> RoomOffsets = []; // roomNo → file offset
    // private ScummBlock _rnamBlock;
    public static readonly List<string> RoomNames = [];
    public static readonly Dictionary<int, string> RoomLookup = []; // fast reverse lookup


}