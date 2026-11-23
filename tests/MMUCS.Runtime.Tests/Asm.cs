using MMUCS.Runtime.Scripts;

namespace MMUCS.Runtime.Tests;

/// <summary>Writes v8 bytecode: an opcode byte, then 32-bit little-endian operands; jumps count from after their offset.</summary>
internal sealed class Asm
{
    private readonly List<byte> _code = [];
    private readonly Dictionary<string, int> _labels = [];
    private readonly List<(int At, string Label)> _fixups = [];

    public int Here => _code.Count;

    public Asm Op(byte opcode) { _code.Add(opcode); return this; }
    public Asm Word(int v) { _code.AddRange(BitConverter.GetBytes(v)); return this; }
    public Asm Op(byte opcode, int operand) => Op(opcode).Word(operand);
    public Asm Sub(byte opcode, byte sub, int? operand = null) { Op(opcode); _code.Add(sub); return operand is int o ? Word(o) : this; }
    public Asm Push(int v) => Op(0x01, v);
    public Asm PushVar(int v) => Op(0x02, v);
    public Asm Write(int v) => Op(0x6d, v);
    public Asm List(params int[] items) { foreach (int i in items) Push(i); return Push(items.Length); }
    public Asm Label(string name) { _labels[name] = Here; return this; }
    public Asm Jump(byte opcode, string label) { Op(opcode); _fixups.Add((Here, label)); return Word(0); }
    public Asm Break() => Op(0x67);
    public Asm End() => Op(0x7b);

    public ScriptCode Global(int number) => new() { Kind = ScriptKind.Global, Number = number, Room = 0, Code = Build(), Entry = 0 };

    private byte[] Build()
    {
        var bytes = _code.ToArray();
        foreach (var (at, label) in _fixups)
            BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), _labels[label] - (at + 4));
        return bytes;
    }
}
