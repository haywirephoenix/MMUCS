using System;
using System.Globalization;

namespace MMUCS.Core.Scumm;

/// <summary>
/// Loosely-typed metadata value (replaces Godot.Variant). Stores the original boxed value and
/// converts on demand, so a value stored as uint can still be read back as (int).
/// </summary>
public readonly struct MetaValue : IEquatable<MetaValue>
{
    public object Value { get; }

    public MetaValue(object value) => Value = value;

    public static MetaValue From<T>(T value) => new(value);

    public bool IsNull => Value is null;
    public Type ValueType => Value?.GetType();

    public static implicit operator MetaValue(bool v) => new(v);
    public static implicit operator MetaValue(byte v) => new(v);
    public static implicit operator MetaValue(short v) => new(v);
    public static implicit operator MetaValue(ushort v) => new(v);
    public static implicit operator MetaValue(int v) => new(v);
    public static implicit operator MetaValue(uint v) => new(v);
    public static implicit operator MetaValue(long v) => new(v);
    public static implicit operator MetaValue(float v) => new(v);
    public static implicit operator MetaValue(double v) => new(v);
    public static implicit operator MetaValue(string v) => new(v);

    public static explicit operator bool(MetaValue m) => Convert.ToBoolean(m.Value, CultureInfo.InvariantCulture);
    public static explicit operator byte(MetaValue m) => unchecked((byte)Convert.ToInt64(m.Value, CultureInfo.InvariantCulture));
    public static explicit operator short(MetaValue m) => unchecked((short)Convert.ToInt64(m.Value, CultureInfo.InvariantCulture));
    public static explicit operator ushort(MetaValue m) => unchecked((ushort)Convert.ToInt64(m.Value, CultureInfo.InvariantCulture));
    public static explicit operator int(MetaValue m) => unchecked((int)Convert.ToInt64(m.Value, CultureInfo.InvariantCulture));
    public static explicit operator uint(MetaValue m) => unchecked((uint)Convert.ToInt64(m.Value, CultureInfo.InvariantCulture));
    public static explicit operator long(MetaValue m) => Convert.ToInt64(m.Value, CultureInfo.InvariantCulture);
    public static explicit operator float(MetaValue m) => Convert.ToSingle(m.Value, CultureInfo.InvariantCulture);
    public static explicit operator double(MetaValue m) => Convert.ToDouble(m.Value, CultureInfo.InvariantCulture);
    public static explicit operator string(MetaValue m) => m.Value?.ToString();

    public bool Equals(MetaValue other) => Equals(Value, other.Value);
    public override bool Equals(object obj) => obj is MetaValue m && Equals(m);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public override string ToString() => Value switch
    {
        null => "",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Value.ToString()
    };
}
