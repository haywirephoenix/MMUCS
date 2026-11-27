using MMUCS.Runtime.Scripts;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// Objects' code runs as ScummVM's runObjectScript runs it: the verb's entry (or the default), from the
/// current room's copy, or from the home room's once an actor carries the object; and the owner, state,
/// class and inventory opcodes keep ScummVM's tables.
/// </summary>
public class ObjectScriptTests
{
    private const int Local = (int)VmState.LocalFlag;
    private const int Ego = 3, Room = 11, Elsewhere = 20, Item = 336;

    // Object 336 in room 11: verb 2 writes var100 = local0, verb 5 counts var101 up once a frame forever,
    // and the default writes var102 = 1.
    private static readonly ObjectCode Arm = Asm.Object(Item, Room,
        (2, new Asm().PushVar(Local | 0).Write(100).End()),
        (5, new Asm().Label("top").Op(0x6e, 101).Break().Jump(0x66, "top")),
        (-1, new Asm().Push(1).Write(102).End()));

    private static (ScriptVm Vm, List<string> Warnings) Machine(params ScriptCode[] scripts)
    {
        // Every object's home room is room 11.
        var limits = GameLimits.Comi with { IndexObjects = [.. Enumerable.Repeat(((byte)0, (byte)Room, 0u), GameLimits.Comi.GlobalObjects)] };
        var vm = new ScriptVm(ScriptStore.Of(scripts, [Arm]), new VmState(limits)) { CurrentRoom = Room };
        vm.State[EngineVars.Ego] = Ego;
        var warnings = new List<string>();
        vm.Warned += warnings.Add;
        vm.Faulted += f => throw new Xunit.Sdk.XunitException(f.ToString());
        return (vm, warnings);
    }

    [Fact]
    public void A_verb_runs_its_own_entry_or_the_default()
    {
        var (vm, warnings) = Machine();
        vm.RunObjectScript(Item, 2, args: [42]);
        Assert.Equal(42, vm.State[100]);
        vm.RunObjectScript(Item, 9); // not listed: the default
        Assert.Equal(1, vm.State[102]);
        Assert.Empty(warnings);

        // Elsewhere the room has no such object: a warning, nothing runs.
        vm.CurrentRoom = Elsewhere;
        vm.RunObjectScript(Item, 2, args: [7]);
        Assert.Equal(42, vm.State[100]);
        Assert.Single(warnings);
    }

    [Fact]
    public void Start_object_takes_flags_object_verb_and_args_and_stop_object_ends_it()
    {
        // start-object 336 verb 5; stop-script 336 leaves it (only object code is stopped by stop-object)
        var code = new Asm()
            .Push(0).Push(Item).Push(5).List().Op(0x7f)
            .Push(Item).Op(0x7c)
            .End().Global(1);
        var (vm, _) = Machine(code);
        vm.RunScript(1);
        Assert.Equal(1, vm.State[101]);
        var slot = Assert.Single(vm.State.Slots, s => s.IsAlive);
        Assert.Equal((ScriptKind.Object, Item, 5), (slot.Kind, slot.Number, slot.Verb));
        Assert.False(vm.IsScriptRunning(Item)); // object code isn't a script

        vm.RunAllScripts();
        Assert.Equal(2, vm.State[101]);
        vm.StopObjectScript(Item);
        Assert.DoesNotContain(vm.State.Slots, s => s.IsAlive);
    }

    [Fact]
    public void Picking_up_puts_it_in_the_inventory_where_its_code_still_runs_from_its_home_room()
    {
        // The inventory script (global 2) records the object it was run for; global 1 picks the arm up
        // and reads the ego's inventory count and first item.
        var inventoryScript = new Asm().PushVar(Local | 0).Write(110).End().Global(2);
        var pickUp = new Asm()
            .Push(Item).Op(0xa6)
            .PushVar(EngineVars.Ego).Op(0xe0).Write(111)
            .PushVar(EngineVars.Ego).Push(1).Op(0xdf).Write(112)
            .End().Global(1);
        var (vm, _) = Machine(pickUp, inventoryScript);
        var objects = vm.State.Objects;
        vm.State[EngineVars.InventoryScript] = 2;
        vm.RunScript(1);
        Assert.Equal((Ego, 1, true), (objects.Owner(Item), objects.State(Item), objects.HasClass(Item, ObjectTables.Untouchable)));
        Assert.Equal((Item, 1, Item), (vm.State[110], vm.State[111], vm.State[112]));

        // Carried, its code comes from its home room wherever the ego is.
        vm.CurrentRoom = Elsewhere;
        vm.RunObjectScript(Item, 2, args: [5]);
        Assert.Equal(5, vm.State[100]);

        // Given to nobody: its code stops, it leaves the inventory (the gap closes) and the script runs again.
        objects.AddToInventory(337);
        objects.SetOwner(337, Ego);
        vm.RunObjectScript(Item, 5);
        vm.State[110] = 0;
        vm.SetOwner(Item, 0);
        Assert.DoesNotContain(vm.State.Slots, s => s.IsAlive && s.Number == Item);
        Assert.Equal(337, objects.Inventory[0]);
        Assert.Equal((Item, 1), (vm.State[110], objects.InventoryCount(Ego)));
        Assert.Null(vm.ObjectCodeOf(Item)); // owned by 0, not in the room: nowhere
    }

    [Fact]
    public void Name_of_renames_an_object_from_any_room_until_no_one_owns_it()
    {
        // Run from another room, as Puerto Pollo names the Plunder map's doors: name-of 336 is "…", then
        // the inventory script (global 2) runs with 0.
        var rename = new Asm().Push(Item).Op(0xb5).Text("/TWNN389/Puerto Pollo").End().Global(1);
        var inventoryScript = new Asm().PushVar(Local | 0).Write(110).Push(1).Write(111).End().Global(2);
        var (vm, _) = Machine(rename, inventoryScript);
        vm.State[EngineVars.InventoryScript] = 2;
        vm.CurrentRoom = Elsewhere;
        vm.RunScript(1);
        Assert.Equal((0, 1), (vm.State[110], vm.State[111]));

        vm.StartScene(Room);
        Assert.Equal("Puerto Pollo", vm.ConvertMessage(vm.NameOf(Item), out string tag));
        Assert.Equal("TWNN389", tag);

        // Owned by no one, the name goes when the room is left.
        vm.State.Objects.SetOwner(Item, 0);
        vm.StartScene(Elsewhere);
        Assert.Null(vm.State.Objects.NewName(Item));
    }

    [Fact]
    public void Classes_set_clear_and_test_with_bit_7()
    {
        // class-of 336 is [151, 160] (set 23 and 32), then [23] (clear 23);
        // var100 = class-of(336) is [160, 23]; var101 = class-of(336) is [160, 151]
        var code = new Asm()
            .Push(Item).List(151, 160).Op(0x89)
            .Push(Item).List(23).Op(0x89)
            .Push(Item).List(160, 23).Op(0xd0).Write(100)
            .Push(Item).List(160, 151).Op(0xd0).Write(101)
            .Push(Item).Push(4).Op(0x8a)
            .Push(Item).Op(0xd1).Write(102)
            .End().Global(1);
        var (vm, _) = Machine(code);
        vm.RunScript(1);
        Assert.Equal((1, 0, 4), (vm.State[100], vm.State[101], vm.State[102]));
        Assert.Equal(1u << 31, vm.State.Objects.Classes(Item));
    }

    [Fact]
    public void Valid_verb_gives_where_the_entry_is_or_0()
    {
        var code = new Asm()
            .Push(Item).Push(2).Op(0xda).Write(100)
            .Push(Item).Push(77).Op(0xda).Write(101) // the default answers
            .Push(999).Push(2).Op(0xda).Write(102)   // no such object here
            .End().Global(1);
        var (vm, _) = Machine(code);
        vm.RunScript(1);
        Assert.Equal(Arm.CodeStart + Arm.EntryFor(2), vm.State[100]);
        Assert.Equal(Arm.CodeStart + Arm.EntryFor(-1), vm.State[101]);
        Assert.Equal(0, vm.State[102]);
    }
}
