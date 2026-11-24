using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// Floating and blast objects as ScummVM's engine has them: load-object brings an object from its home room
/// into the current room's slots, super-blast-object draws one over the room for a frame, kernel-get 216 finds
/// the blasted object under a point, locked floating objects stay through a room change, and a floating
/// object's code comes from its home room. The game tests (MMUCS_GAME) open COMI's inventory and verb coin,
/// and pick up an item as the cursor.
/// </summary>
public class FloatingObjectTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    /// <summary>Object 120 lives in room 1 (an inventory icon); rooms 5 and 6 are where the game is.</summary>
    private static ScriptVm Machine(params ScriptCode[] scripts)
    {
        var limits = GameLimits.Comi with { IndexObjects = [.. Enumerable.Range(0, 200).Select(i => ((byte)0, (byte)(i == 120 ? 1 : 0), 0u))] };
        var store = ScriptStore.Of(scripts, [], new Dictionary<int, (int, int)> { [5] = (640, 480), [6] = (640, 480) },
            new Dictionary<int, IReadOnlyList<ObjectPlace>>
            {
                [1] = [new ObjectPlace(1, 119, 0, 0, 0, 0, 10, 10), new ObjectPlace(2, 120, 1, 1, 30, 40, 80, 56)],
                [5] = [new ObjectPlace(1, 150, 0, 0, 0, 0, 640, 480)],
            });
        var vm = new ScriptVm(store, new VmState(limits));
        vm.Faulted += f => throw new Xunit.Sdk.XunitException(f.ToString());
        vm.StartScene(5);
        return vm;
    }

    [Fact]
    public void Load_object_brings_an_object_from_its_home_room_and_blasting_it_draws_it_for_a_frame()
    {
        var code = new Asm()
            .Push(120).Sub(0xaa, 0x3E).Push(120).Sub(0xaa, 0x3E)     // load-object 120, twice: once is enough
            .Push(120).Op(0xf0).Write(300)                            // image-x 120: it's in the room's slots now
            .List(119, 120, 100, 180, 0, 0, 255, 255, 1).Op(0xba)     // super-blast-object 120 at 100,180, image 1
            .List(216, 120, 200).Op(0xd8).Write(301)                  // the blasted object under 120,200
            .List(216, 10, 10).Op(0xd8).Write(302)                    // and under 10,10: none
            .Label("again").Break().Jump(0x66, "again").Global(10);
        var vm = Machine(code);
        vm.RunScript(10);
        Assert.Equal((30, 120, 0), (vm.State[300], vm.State[301], vm.State[302]));
        Assert.True(vm.IsFloating(120));
        var slot = Assert.Single(vm.RoomObjects, o => o.Id == 120);
        Assert.Equal((2, 0), (slot.Slot, slot.Parent)); // after the room's own, belonging to nothing here

        vm.EndFrameTexts();
        Assert.Equal(new BlastObject(120, 100, 180, 80, 56, 255, 255, 1, 0), Assert.Single(vm.BlastObjects));
        new FrameLoop(vm).RunFrame(5); // the script only breaks: nothing blasted this frame
        Assert.Empty(vm.BlastObjects);
    }

    [Fact]
    public void A_locked_floating_object_stays_through_a_room_change_and_an_unlocked_one_goes()
    {
        var load = new Asm().Push(120).Sub(0xaa, 0x3E).End().Global(10);
        var lockIt = new Asm().List(11, 120).Op(0xba).End().Global(11);
        var vm = Machine(load, lockIt);
        vm.RunScript(10);
        vm.StartScene(6);
        Assert.DoesNotContain(vm.RoomObjects, o => o.Id == 120);

        vm.StartScene(5);
        vm.RunScript(10);
        vm.RunScript(11);
        vm.StartScene(6);
        Assert.Contains(vm.RoomObjects, o => o.Id == 120);
        Assert.True(vm.IsFloating(120));
    }

    [Fact]
    public void Blasting_an_object_that_isn_t_in_the_room_is_a_fault()
    {
        var code = new Asm().List(119, 120, 0, 0, 0, 0, 255, 255, 1).Op(0xba).End().Global(10);
        var limits = GameLimits.Comi;
        var vm = new ScriptVm(ScriptStore.Of(code), new VmState(limits));
        var faults = new List<ScriptFault>();
        vm.Faulted += faults.Add;
        vm.RunScript(10);
        Assert.Contains("blast object 120", Assert.Single(faults).Message);
    }

    /// <summary>Boots, clicks Mega-Monkey, skips the opening, and plays until the first scene's talk is over.</summary>
    private static ScriptedGame ToTheCannon()
    {
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var game = new ScriptedGame();
        game.Load(root, Game);
        game.Play();
        game.SetMouseCaptured(true);
        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 224)));
        for (int i = 0; i < 60; i++) game.Tick();
        game.Press(MouseButton.Left);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Left);
        for (int i = 0; i < 60 * 200 && game.Loop.Jiffies < 60 * 140; i++)
        {
            if (game.IsVideoPlaying) game.SkipVideo();
            game.Tick();
        }
        return game;
    }

    [Fact]
    public void COMI_s_inventory_is_blasted_floating_objects_and_its_verb_coin_an_actor_over_them()
    {
        if (Game == null) return;
        using var game = ToTheCannon();
        Assert.Equal(9, game.CurrentRoom);

        // The inventory: a right click; its background and items are floating objects blasted every frame.
        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 300)));
        game.Press(MouseButton.Right);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Right);
        for (int i = 0; i < 30; i++) game.Tick();
        var blasted = game.Vm.BlastObjects.Select(b => b.Object).ToList();
        Assert.Contains(114, blasted); // inventory-bg-object
        Assert.Contains(120, blasted); // two-balloons-icon-object
        Assert.All(blasted, o => Assert.True(game.Vm.IsFloating(o)));

        // Closed again, then the verb coin: held on the cannon, actor 50 shows over everything at the click.
        game.Press(MouseButton.Right);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Right);
        for (int i = 0; i < 30; i++) game.Tick();
        game.MoveCursor(GameState.PageToGame(new GamePoint(152, 268)));
        game.Press(MouseButton.Left);
        for (int i = 0; i < 40; i++) game.Tick();
        // Held on the cannon it's the cannon that's clicked, not the full-screen overlay actor over it, so
        // its name stays up as the label.
        Assert.Contains(game.Vm.ScreenTexts, t => t.Text == "cannon");
        var coin = Assert.Single(game.Vm.Actors.UpperPictures);
        Assert.Equal(50, coin.Actor);
        Assert.Equal(1, game.Vm.Actors[50].Costume);
        Assert.DoesNotContain(game.Vm.Actors.Pictures, p => p.Actor == 50);
        game.Release(MouseButton.Left);
        Assert.Equal(0, game.Faults);
    }

    [Fact]
    public void A_floating_object_s_code_comes_with_it_from_its_home_room()
    {
        // Object 120's code is in room 1 (its verb 93 marks var300); it's loaded into room 5 and its verb run there.
        var limits = GameLimits.Comi with { IndexObjects = [.. Enumerable.Range(0, 200).Select(i => ((byte)0, (byte)(i == 120 ? 1 : 0), 0u))] };
        var store = ScriptStore.Of(
            [new Asm().Push(120).Sub(0xaa, 0x3E).Push(0).Push(120).Push(93).List().Op(0x7f).End().Global(10)],
            [Asm.Object(120, 1, (93, new Asm().Push(1).Write(300).End()))],
            new Dictionary<int, (int, int)> { [5] = (640, 480) },
            new Dictionary<int, IReadOnlyList<ObjectPlace>> { [1] = [new ObjectPlace(1, 120, 0, 0, 30, 40, 80, 56)] });
        var vm = new ScriptVm(store, new VmState(limits));
        vm.Faulted += f => throw new Xunit.Sdk.XunitException(f.ToString());
        var warnings = new List<string>();
        vm.Warned += warnings.Add;
        vm.StartScene(5);
        vm.RunScript(10);
        Assert.Equal(1, vm.State[300]);
        Assert.Empty(warnings);
    }

    [Fact]
    public void An_inventory_item_clicked_becomes_the_cursor_through_closing_the_inventory_until_a_right_click()
    {
        if (Game == null) return;
        using var game = ToTheCannon();
        void Click(MouseButton button, double x, double y)
        {
            game.MoveCursor(GameState.PageToGame(new GamePoint(x, y)));
            game.Press(button);
            for (int i = 0; i < 6; i++) game.Tick();
            game.Release(button);
            for (int i = 0; i < 30; i++) game.Tick();
        }
        Click(MouseButton.Right, 320, 300); // the inventory
        var balloons = Assert.Single(game.Vm.BlastObjects, b => b.Object == 120);
        Click(MouseButton.Left, balloons.X + balloons.Width / 2, balloons.Y + balloons.Height / 2);
        Assert.Equal(120, game.Vm.CursorImage.Object); // the item's own code (from its home room) made it the cursor

        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 20))); // off the inventory: it closes
        for (int i = 0; i < 60; i++) game.Tick();
        Assert.DoesNotContain(game.Vm.BlastObjects, b => b.Object == 114);
        Assert.Equal(120, game.Vm.CursorImage.Object); // still holding the balloons

        Click(MouseButton.Right, 320, 200);
        Assert.Equal(105, game.Vm.CursorImage.Object); // back to the system cursor
        Assert.Equal(0, game.Faults);
    }

    [Fact]
    public void Object_names_in_messages_come_from_the_object_s_room()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var vm = new ScriptVm(new ScriptStore(root), new VmState(GameLimits.Read(root)));
        vm.State[300] = 276; // the cannon
        byte[] message = [.. "/SSBT023/"u8, 0xFF, 6, .. BitConverter.GetBytes(300), 0];
        Assert.Equal("cannon", vm.ConvertMessage(message, out _));
        Assert.Equal("/CNNZ069/cannon", System.Text.Encoding.Latin1.GetString(vm.NameOf(276)).TrimEnd('\0'));
    }
}
