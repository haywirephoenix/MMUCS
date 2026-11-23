using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;
using Xunit.Abstractions;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// The script machine's state follows v8's rules (ScummVM script_v8.cpp, script_v6.cpp): how variable
/// numbers address globals, bits and locals, how arrays are defined, laid out and dropped, and how slots
/// start. The game checks need MMUCS_GAME.
/// </summary>
public class VmStateTests(ITestOutputHelper output)
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private const int Bit = unchecked((int)VmState.BitFlag), Local = (int)VmState.LocalFlag;

    private static VmState Small() => new(GameLimits.Comi with { Arrays = 4 });

    [Fact]
    public void The_game_s_limits_and_index_arrays_are_read()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var limits = GameLimits.Read(root);
        Assert.NotNull(limits);
        output.WriteLine(limits.ToString());
        output.WriteLine($"index arrays: {string.Join(", ", limits.IndexArrays.Select(a => $"var{a.Variable}[{a.Dim2},{a.Dim1}]"))}");
        Assert.Equal(GameLimits.Comi, limits with { IndexArrays = GameLimits.Comi.IndexArrays });

        // Each index array exists from the start, in order from id 1, with the size the index gives.
        var vm = new VmState(limits);
        int id = 1;
        foreach (var (variable, dim2, dim1) in limits.IndexArrays)
        {
            Assert.Equal(id++, vm.Read(variable));
            var array = vm.Array(variable);
            Assert.Equal(ArrayType.Int, array.Type);
            Assert.Equal((dim1 + 1) * (dim2 + 1), array.Length);
        }
    }

    [Fact]
    public void Variable_numbers_address_globals_bits_and_the_running_script_s_locals()
    {
        var vm = Small();
        vm.Write(303, 8);
        Assert.Equal(8, vm[303]);
        Assert.Equal(8, vm.Read(303));

        vm.Write(Bit | 9, 5); // any non-zero value sets a bit
        Assert.True(vm.Bit(9));
        Assert.Equal(1, vm.Read(Bit | 9));
        Assert.Equal(0, vm.Read(9)); // global 9 is separate
        vm.Write(Bit | 9, 0);
        Assert.False(vm.Bit(9));

        // Locals belong to the running script.
        Assert.Throws<ScriptException>(() => vm.Read(Local | 0));
        var a = vm.Slots[1];
        var b = vm.Slots[2];
        vm.Current = a;
        vm.Write(Local | 3, 33);
        vm.Write(Local | 25, 7); // 25 is the last one
        vm.Current = b;
        Assert.Equal(0, vm.Read(Local | 3));
        vm.Current = a;
        Assert.Equal(33, vm.Read(Local | 3));
        Assert.Equal(33, a.Locals[3]);

        Assert.Throws<ScriptException>(() => vm.Read(Local | 26));
        Assert.Throws<ScriptException>(() => vm.Read(1500));
        Assert.Throws<ScriptException>(() => vm.Write(Bit | 2048, 1));
        Assert.Throws<ScriptException>(() => vm.Read(0x20000000)); // no meaning in v8
    }

    [Fact]
    public void Arrays_are_named_by_a_variable_holding_their_id()
    {
        var vm = Small();
        var array = vm.DefineArray(100, ArrayType.Int, 2, 4); // [0..2][0..4]: dim1 5, dim2 3
        Assert.Equal(1, vm[100]);
        Assert.Same(array, vm.Array(100));
        Assert.Equal((5, 3, 15), (array.Dim1, array.Dim2, array.Length));

        // Element (index, base) is at base + index × dim1.
        array[1, 2] = 42;
        Assert.Equal(42, array.Ints[2 + 1 * 5]);
        Assert.Equal(42, array[0, 7]);
        Assert.Throws<ScriptException>(() => array[3, 0]);
        Assert.Throws<ScriptException>(() => array[0, -1]);

        // Ids are the lowest free ones; redefining drops the old array first, so it gets its id back.
        var second = vm.DefineArray(101, ArrayType.String, 0, 9);
        Assert.Equal(2, vm[101]);
        var again = vm.DefineArray(100, ArrayType.Int, 0, 0);
        Assert.Equal(1, vm[100]);
        Assert.NotSame(array, again);
        vm.NukeArray(101);
        Assert.Equal(0, vm[101]);
        Assert.Null(vm.Array(101));
        Assert.Null(vm.ArrayById(2));

        // String arrays hold bytes.
        var s = vm.DefineArray(101, ArrayType.String, 0, 5);
        "hi!"u8.CopyTo(s.Bytes);
        s[0, 1] = 0x1_49; // a byte: 0x49
        Assert.Equal("hI!", s.Text);

        // A local can point at an array too; bits can't. Ids run out at the limit (4 → ids 1..3).
        vm.Current = vm.Slots[1];
        vm.DefineArray(Local | 0, ArrayType.Int, 0, 1);
        Assert.Equal(3, vm.Slots[1].Locals[0]);
        Assert.Throws<ScriptException>(() => vm.DefineArray(Bit | 1, ArrayType.Int, 0, 1));
        Assert.Throws<ScriptException>(() => vm.DefineArray(102, ArrayType.Int, 0, 1));
    }

    [Fact]
    public void Slots_start_from_1_and_set_25_locals()
    {
        var vm = Small();
        var code = new ScriptCode { Kind = ScriptKind.Global, Number = 1, Room = 1, Code = new byte[8], Entry = 0 };
        var slot = vm.FreeSlot();
        Assert.Equal(1, slot.Index);

        slot.Locals[0] = 9;
        slot.Locals[25] = 77;
        slot.Push(5);
        slot.Start(code, [10, 20]);
        Assert.True(slot.IsAlive);
        Assert.Equal(SlotStatus.Running, slot.Status);
        Assert.Equal((ScriptKind.Global, 1, 0), (slot.Kind, slot.Number, slot.Pc));
        Assert.Equal([10, 20, 0], slot.Locals[..3]);
        Assert.Equal(77, slot.Locals[25]); // ScummVM sets only the first 25
        Assert.Empty(slot.Stack);
        Assert.Equal(2, vm.FreeSlot().Index);

        slot.Push(1);
        slot.Push(2);
        Assert.Equal(2, slot.Pop());
        Assert.Equal(1, slot.Pop());
        Assert.Throws<ScriptException>(() => slot.Pop());

        slot.Kill();
        Assert.False(slot.IsAlive);
        Assert.Equal(1, vm.FreeSlot().Index);
    }
}
