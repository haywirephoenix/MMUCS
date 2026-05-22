using System.Collections.Generic;

public static class ScummStatics
{
    public static readonly Dictionary<long, int> RoomOffsets = new(); // roomNo → file offset
    // private ScummBlock _rnamBlock;
    public static List<string> RoomNames = new();
    public static Dictionary<int, string> RoomLookup = new(); // fast reverse lookup
}