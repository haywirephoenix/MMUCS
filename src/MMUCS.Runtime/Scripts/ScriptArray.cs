using System;

namespace MMUCS.Runtime.Scripts;

/// <summary>What an array holds: 32-bit numbers, or a string's bytes (v6+ <c>kIntArray</c> 5, <c>kStringArray</c> 4).</summary>
public enum ArrayType
{
    String = 4,
    Int = 5,
}

/// <summary>
/// A script array. Scripts name an array by a variable that holds its id; this is what the id points at.
/// <see cref="Dim1"/> and <see cref="Dim2"/> are the element counts (the defined sizes + 1), and element
/// (index, base) is at base + index × Dim1 (ScummVM <c>readArray</c> / <c>writeArray</c>).
/// </summary>
public sealed class ScriptArray
{
    private readonly int[] _ints;
    private readonly byte[] _bytes;

    public ScriptArray(ArrayType type, int dim2, int dim1)
    {
        if (dim1 < 0 || dim2 < 0) throw new ScriptException($"array dimensions [{dim2}, {dim1}] are negative");
        Type = type;
        Dim1 = dim1 + 1;
        Dim2 = dim2 + 1;
        if (type == ArrayType.Int) _ints = new int[Length];
        else _bytes = new byte[Length];
    }

    public ArrayType Type { get; }
    public int Dim1 { get; }
    public int Dim2 { get; }
    public int Length => Dim1 * Dim2;

    public int this[int index, int @base]
    {
        get
        {
            int at = Offset(index, @base, "reading");
            return _ints != null ? _ints[at] : _bytes[at];
        }
        set
        {
            int at = Offset(index, @base, "writing");
            if (_ints != null) _ints[at] = value;
            else _bytes[at] = (byte)value;
        }
    }

    /// <summary>A string array's bytes (empty for an int array).</summary>
    public Span<byte> Bytes => _bytes;

    /// <summary>An int array's values (empty for a string array).</summary>
    public Span<int> Ints => _ints;

    /// <summary>A string array as text, up to its first 0.</summary>
    public string Text
    {
        get
        {
            if (_bytes == null) return "";
            int n = Array.IndexOf(_bytes, (byte)0);
            return System.Text.Encoding.Latin1.GetString(_bytes, 0, n < 0 ? _bytes.Length : n);
        }
    }

    private int Offset(int index, int @base, string what)
    {
        long at = @base + (long)index * Dim1;
        if (at < 0 || at >= Length)
            throw new ScriptException($"array {what} out of bounds: [{@base}, {index}] exceeds [{Dim1}, {Dim2}]");
        return (int)at;
    }
}

/// <summary>A script did something the engine treats as fatal (a bad variable, an array out of bounds, a stack underflow).</summary>
public sealed class ScriptException(string message) : Exception(message);
