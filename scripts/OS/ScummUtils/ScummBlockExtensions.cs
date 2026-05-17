using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

public static class ScummBlockExtensions
{
    
    
    private static bool IsSliceValid(ReadOnlySpan<byte> span, int offset)
    {
        return span.Length > 0 && offset < span.Length;
    }
    public static uint Get(this ReadOnlySpan<byte> span, int offset){
        if(!IsSliceValid(span, offset)) return 0;
        return span[offset];
    }
    
    public static ushort U16LE(this ReadOnlySpan<byte> span, int offset)
    {
        if(!IsSliceValid(span, offset)) return 0;
        return BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
    }
    public static uint U32LE(this ReadOnlySpan<byte> span, int offset)
    {
        if(!IsSliceValid(span, offset)) return 0;
        return BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
    }
    public static uint U32BE(this ReadOnlySpan<byte> span, int offset)
    {
        if (offset < 0 || offset + 4 > span.Length) return 0;
        return BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, 4));
    }

    

    public static short S16LE(this ReadOnlySpan<byte> span, int offset)
    {
        if(!IsSliceValid(span, offset)) return 0;
        return BinaryPrimitives.ReadInt16LittleEndian(span.Slice(offset, 2));
    }
    
    public static int S32LE(this ReadOnlySpan<byte> span, int offset)
    {
        if (!IsSliceValid(span, offset)) return 0;
        return BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
    }
    
    /// <summary>
    /// Helper to convert a raw byte span into a ushort array (Little Endian).
    /// </summary>
    public static ushort[] ReadU16Table(this ReadOnlySpan<byte> span)
    {
        int count = span.Length / 2;
        var table = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            table[i] = span.U16LE(i * 2);
        }
        return table;
    }


}