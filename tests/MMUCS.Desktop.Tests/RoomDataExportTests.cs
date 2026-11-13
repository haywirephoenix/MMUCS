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

    [Fact]
    public void Constant_expressions_nest()
    {
        var s = Script(100, 20, 5, Add, Sub);
        Assert.True(RoomActors.Const(s, s.Length, out int v));
        Assert.Equal(75, v);
        Assert.False(RoomActors.Const(Script(new byte[] { 0x02, 1, 0, 0, 0 }), 29, out _)); // a variable
    }
}
