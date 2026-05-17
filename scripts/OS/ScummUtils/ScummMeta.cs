using System;
using System.Collections.Generic;
public static class ScummMeta
{
    public static readonly Dictionary<uint, Type> TagToEnum = new()
    {
        { ScummTag.AKOS, typeof(AKHD) },
        { ScummTag.AKHD, typeof(AKHD) }
    };
    public enum AKHD
    {
        VersionNo,
        Flags,
        ChoreCount,
        CelsCount,
        Codec,
        LayerCount,
        Mirror,
        HasManyDirections
    }
    
    public enum LFLF
    {
        roomNo,
        roomName,
    }
    public enum RMHD
    {
        version,
        width,
        height,
        numObjects,
        zBuffer,
        transpar
    }
    public enum OBIM
    {
        name,
        version,
        imageCount,
        x,
        y,
        width,
        height,
        actorDir,
        flags
    }
    public enum CDHD
    {
        objId,
        flags,
        walkX,
        walkY,
        actorDir
    } 
    
    public enum OBNA
    {
        name
    }
    
    public struct BOXD
    {
        public int id;
        public int[] corners;
    }
    
}