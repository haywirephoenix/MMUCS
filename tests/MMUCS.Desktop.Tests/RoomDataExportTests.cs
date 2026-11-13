using System;
using System.Collections.Generic;
using MMUCS.Core.Scumm;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>The helpers behind the Room data export (data/rooms/*.json, read by tools/mroom).</summary>
public class RoomDataExportTests
{
    [Fact]
    public void Class_bits_become_one_based_class_numbers()
    {
        Assert.Equal(new[] { 6 }, RoomDataExport.ClassList(0x20));
        Assert.Equal(new[] { 1, 32 }, RoomDataExport.ClassList(0x80000001));
        Assert.Empty(RoomDataExport.ClassList(0));
    }

    [Theory]
    [InlineData("/TWNZ518/plaque", "plaque")]
    [InlineData("plaque", "plaque")]
    [InlineData("/broken", "/broken")]
    [InlineData("", "")]
    public void Message_codes_are_stripped_from_hover_names(string raw, string expected)
        => Assert.Equal(expected, RoomDataExport.StripMessageCode(raw));
}

/// <summary>RoomActors reading costume placements out of script byte code.</summary>
public class RoomActorsScanTests
{
    private static byte[] Script(params object[] parts)
    {
        // Padding first: the scanner looks back from each op, so nothing may start at 0.
        var b = new List<byte>(new byte[24]);
        foreach (var part in parts)
        {
            if (part is int v) { b.Add(0x01); b.AddRange(BitConverter.GetBytes(v)); } // push-word
            else if (part is byte[] raw) b.AddRange(raw);
        }
        return b.ToArray();
    }

    private static readonly byte[] SetActor = { 0xAC, 0x7A }, SetCostume = { 0xAC, 0x64 }, Elevation = { 0xAC, 0x6E },
        Put = { 0xA1 }, Add = { 0x0E }, Sub = { 0x0F };

    [Fact]
    public void Constant_sums_are_read_as_positions_and_elevation_lifts_the_drawing()
    {
        // actor 3 costume 35; actor 3 elevation 300; put-actor 3 at 320,492 + 300 in-room 10 (cannon-v)
        var s = Script(3, SetActor, 35, SetCostume, 300, Elevation, 3, 320, 492, 300, Add, 10, Put);
        var found = Assert.Single(RoomActors.ScanScript(s, 10));
        Assert.Equal((3, 35, 320, 792, 300), (found.Actor, found.Costume, found.X, found.Y, found.Elevation));
    }

    [Fact]
    public void An_elevation_set_after_the_placement_does_not_move_it()
    {
        // put-actor 2 at 320,240; then elevation 240 and a re-place at y-of + 240 (only its depth changes)
        var s = Script(2, SetActor, 92, SetCostume, 2, 320, 240, 16, Put, 2, SetActor, 240, Elevation);
        var found = Assert.Single(RoomActors.ScanScript(s, 16));
        Assert.Equal((320, 240, 0), (found.X, found.Y, found.Elevation));
    }

    private static byte[] PushVar(int var) { var b = new byte[5]; b[0] = 0x02; BitConverter.GetBytes(var).CopyTo(b, 1); return b; }
    private static byte[] Jump(byte op, int offset) { var b = new byte[5]; b[0] = op; BitConverter.GetBytes(offset).CopyTo(b, 1); return b; }
    private const int Local0 = 0x40000000, Local1 = 0x40000001, Local2 = 0x40000002;

    /// <summary>Like script 88: actor local0 gets costume 69 when local2 is nonzero, else 67, then animation 16 and scale local1.</summary>
    private static byte[] Dresser() => Script(
        PushVar(Local0), SetActor,          // 24: actor local0
        PushVar(Local2), Jump(0x65, 12),    // 31: if (local2) { → else at 53
        69, SetCostume, Jump(0x66, 7),      // 41:   costume 69 } → end at 60
        67, SetCostume,                     // 53: else costume 67
        PushVar(Local0), 16, new byte[] { 0xA4 },  // 60: do-animation local0 16
        PushVar(Local1), new byte[] { 0xAC, 0x73 }); // actor local0 scale local1

    [Fact]
    public void A_script_that_dresses_its_parameter_is_read_with_the_test_choosing_the_costume()
    {
        var found = RoomActors.ParamCostumes(Dresser());
        Assert.Equal(2, found.Count);
        Assert.Contains(new RoomActors.ParamCostume(0, 69, 16, 180, 2, true, -1, 1), found);
        Assert.Contains(new RoomActors.ParamCostume(0, 67, 16, 180, 2, false, -1, 1), found);
    }

    [Theory]
    [InlineData(0, 67)]
    [InlineData(1, 69)]
    public void A_call_dresses_the_actor_passed_and_the_next_placement_of_it_places_the_costume(int third, int costume)
    {
        // start-script 88(local2, 150, third); put-actor local2 at 36,155 in-room current-room
        var dresser = RoomActors.ParamCostumes(Dresser());
        var caller = Script(0, 88, PushVar(Local2), 150, third, 3, new byte[] { 0x79 },
                            PushVar(Local2), 36, 155, PushVar(4), Put);
        var found = Assert.Single(RoomActors.ScanScript(caller, 14, no => no == 88 ? dresser : null));
        Assert.Equal((costume, 36, 155, 16, 88, 150), (found.Costume, found.X, found.Y, found.Anim, found.Via, found.Scale));
        Assert.True(found.Actor < 0); // an actor kept in a variable
    }

    [Fact]
    public void Constant_expressions_nest()
    {
        var s = Script(100, 20, 5, Add, Sub);
        Assert.True(RoomActors.Const(s, s.Length, out int v));
        Assert.Equal(75, v);
        Assert.False(RoomActors.Const(Script(new byte[] { 0x02, 1, 0, 0, 0 }), 29, out _)); // a variable
    }
}
