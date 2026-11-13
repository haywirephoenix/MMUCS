using System;
using System.Collections.Generic;
using System.Linq;
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
    public void A_costume_change_on_a_branch_the_goto_skips_does_not_end_the_window()
    {
        // actor 5 costume 166; goto L; actor 5 costume 12; L: actor 5 scale 140; put-actor 5 at 346,130 (voodoo-e's skull)
        var s = Script(5, SetActor, 166, SetCostume, Jump(0x66, 7), 12, SetCostume, 140, new byte[] { 0xAC, 0x73 }, 5, 346, 130, 29, Put);
        var found = RoomActors.ScanScript(s, 29).First(p => p.Costume == 166);
        Assert.Equal((346, 130, 140), (found.X, found.Y, found.Scale));
    }

    private static readonly byte[] NeverZClip = { 0xAC, 0x74 }, AlwaysZClip = { 0xAC, 0x75 }, IgnoreBoxes = { 0xAC, 0x76 };

    [Fact]
    public void The_z_clip_set_while_it_wears_the_costume_counts_even_after_the_placement()
    {
        // never-zclip; put-actor 3 at 320,240; then always-zclip 2 (fortbase's chickens are clipped after they're put)
        var s = Script(3, SetActor, 35, SetCostume, NeverZClip, 3, 320, 240, 10, Put, 3, SetActor, 2, AlwaysZClip);
        Assert.Equal(2, Assert.Single(RoomActors.ScanScript(s, 10)).Clip);
    }

    [Fact]
    public void Ignore_boxes_puts_the_clip_back_to_the_walkbox_mask_and_255_means_the_same()
    {
        var s = Script(3, SetActor, 35, SetCostume, 1, AlwaysZClip, IgnoreBoxes, 3, 320, 240, 10, Put);
        var found = Assert.Single(RoomActors.ScanScript(s, 10));
        Assert.Equal((RoomActors.ClipByBox, true), (found.Clip, found.IgnoreBoxes));
        var s255 = Script(3, SetActor, 35, SetCostume, 255, AlwaysZClip, 3, 320, 240, 10, Put);
        Assert.Equal(RoomActors.ClipByBox, Assert.Single(RoomActors.ScanScript(s255, 10)).Clip);
    }

    private static readonly byte[] FollowBoxes = { 0xAC, 0x77 };

    [Fact]
    public void Always_zclip_minus_one_is_by_box_as_the_engine_keeps_the_clip_in_a_byte()
    {
        // logo's actor 11, McMutton: always-zclip -1
        var s = Script(3, SetActor, 35, SetCostume, -1, AlwaysZClip, 3, 320, 240, 10, Put);
        Assert.Equal(RoomActors.ClipByBox, Assert.Single(RoomActors.ScanScript(s, 10)).Clip);
    }

    [Fact]
    public void Follow_boxes_after_the_placement_does_not_undo_the_clip_it_was_put_with()
    {
        // lc-deck's captain: ignore-boxes, always-zclip 5, put-actor 7 at 144,387 … follow-boxes once the scene is done
        var s = Script(7, SetActor, 191, SetCostume, IgnoreBoxes, 5, AlwaysZClip, 7, 144, 387, 35, Put, 7, SetActor, FollowBoxes);
        var found = Assert.Single(RoomActors.ScanScript(s, 35));
        Assert.Equal((5, true, false), (found.Clip, found.IgnoreBoxes, found.FollowsAtPut));
        // A close-up put while it ignores the boxes keeps box 0 (its mask) though follow-boxes comes later.
        var closeUp = Script(1, SetActor, 173, SetCostume, IgnoreBoxes, 1, 0, 0, 31, Put, 1, SetActor, FollowBoxes);
        var c = Assert.Single(RoomActors.ScanScript(closeUp, 31));
        Assert.Equal((RoomActors.ClipByBox, true, false), (c.Clip, c.IgnoreBoxes, c.FollowsAtPut));
    }

    [Fact]
    public void Only_a_follow_boxes_or_default_before_the_put_says_the_engine_moves_it_into_a_box()
    {
        var follows = Script(1, SetActor, 110, SetCostume, FollowBoxes, 1, 478, 455, 21, Put);
        Assert.True(Assert.Single(RoomActors.ScanScript(follows, 21)).FollowsAtPut);
        // Nothing said here: a room's setup script may have set ignore-boxes (town's Kenny, put at 0,0 by a cutscene).
        var unknown = Script(4, SetActor, 84, SetCostume, 4, 0, 0, 15, Put);
        Assert.False(Assert.Single(RoomActors.ScanScript(unknown, 15)).FollowsAtPut);
    }

    [Fact]
    public void A_costume_set_on_one_branch_gets_the_clip_set_after_both_branches()
    {
        // barber: if (…) costume 114 else costume 112; always-zclip 3; put-actor 4 at 196,329
        var s = Script(4, SetActor, 114, SetCostume, Jump(0x66, 7), 112, SetCostume, 3, AlwaysZClip, 4, 196, 329, 21, Put);
        var found = RoomActors.ScanScript(s, 21);
        Assert.Equal(new[] { (112, 3), (114, 3) }, found.Select(f => (f.Costume, f.Clip)).OrderBy(x => x).ToArray());
    }

    private static readonly byte[] XOf = { 0xEB }, YOf = { 0xEC };

    [Fact]
    public void Elevation_then_a_put_at_y_of_plus_it_keeps_the_picture_and_sorts_by_the_lower_y()
    {
        // cannon's Wally: put-actor 2 at 0,0; elevation 381; put-actor 2 at x-of(2), y-of(2) + 381 in-room current-room
        var s = Script(2, SetActor, 25, SetCostume, 2, 0, 0, 9, Put,
                       2, SetActor, 381, Elevation, 2, 2, XOf, 2, YOf, 381, Add, PushVar(4), Put);
        var found = Assert.Single(RoomActors.ScanScript(s, 9));
        Assert.Equal((0, 381, 381), (found.X, found.Y, found.Elevation));
    }

    [Fact]
    public void A_put_relative_to_where_it_is_without_an_elevation_is_a_later_step_and_is_not_applied()
    {
        // clifface's Guybrush climbs: put-actor 1 at 320,70; … put-actor 1 at x-of(1), y-of(1) + 33
        var s = Script(1, SetActor, 384, SetCostume, 1, 320, 70, 79, Put, 1, 1, XOf, 1, YOf, 33, Add, 79, Put);
        var found = Assert.Single(RoomActors.ScanScript(s, 79));
        Assert.Equal((320, 70, 0), (found.X, found.Y, found.Elevation));
    }

    [Fact]
    public void A_put_in_another_room_ends_what_a_later_relative_put_applies_to()
    {
        // snow-dia: put-actor 5 at 0,518 in-room 82; put-actor 5 at 640,0 in-room 84; elevation 501; put at y-of(5) + 501
        var s = Script(5, SetActor, 413, SetCostume, 5, 0, 518, 82, Put, 5, 640, 0, 84, Put,
                       5, SetActor, 501, Elevation, 5, 5, XOf, 5, YOf, 501, Add, PushVar(4), Put);
        var found = Assert.Single(RoomActors.ScanScript(s, 82));
        Assert.Equal((0, 518, 0), (found.X, found.Y, found.Elevation));
    }

    [Fact]
    public void An_actor_taken_out_and_put_back_while_the_costume_plays_is_one_it_removes()
    {
        // barber: costume 122 put at 320,240; put-actor 4 in room 0; put-actor 4 back at 196,329 (122 draws Bill meanwhile)
        var s = Script(1, SetActor, 122, SetCostume, 1, 320, 240, 21, Put, 4, 0, 0, 0, Put, 4, 196, 329, 21, Put);
        Assert.Equal(new[] { 4 }, RoomActors.ScanScript(s, 21).Single(p => p.Costume == 122).Removes);
        // Taken out for good: a scene ending, not the costume drawing it.
        var gone = Script(1, SetActor, 122, SetCostume, 1, 320, 240, 21, Put, 4, 0, 0, 0, Put);
        Assert.Empty(Assert.Single(RoomActors.ScanScript(gone, 21)).Removes);
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

public class RoomObjectStatesTests
{
    [Theory]
    [InlineData(0, 3, 0)]  // hidden: the first image, should it be shown by hand
    [InlineData(1, 3, 0)]  // state N shows image N
    [InlineData(2, 3, 1)]
    [InlineData(9, 3, 2)]  // past the last image: the last
    [InlineData(2, 0, 0)]
    public void A_state_shows_its_image(int state, int frames, int frame)
        => Assert.Equal(frame, RoomObjectStates.FrameFor(state, frames));
}

public class ZPlanesTests
{
    [Fact]
    public void A_strip_decodes_runs_and_literals_one_byte_per_row()
    {
        var dst = new byte[5];
        // run of 3 x FF, then 2 literal bytes
        ZPlanes.DecompressStrip(new byte[] { 0x83, 0xFF, 0x02, 0x0F, 0xF0 }, dst, strip: 0, strips: 1, height: 5);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x0F, 0xF0 }, dst);
    }

    [Fact]
    public void A_plane_reads_strip_offsets_and_leaves_empty_strips_clear()
    {
        // OFFS header (8 bytes), offsets for 2 strips: strip 0 at 16, strip 1 empty (0); then strip 0's data.
        var d = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 16, 0, 0, 0, 0, 0, 0, 0, 0x82, 0x80 };
        var plane = ZPlanes.DecodePlane(d, strips: 2, height: 2);
        Assert.True(plane.IsSet(0, 0));
        Assert.False(plane.IsSet(1, 0));
        Assert.True(plane.IsSet(0, 1));
        Assert.False(plane.IsSet(8, 0));
    }

    [Fact]
    public void Shown_objects_write_their_planes_over_the_rooms()
    {
        var room = new ZPlane(4, 2);
        Array.Fill(room.Data, (byte)0xFF);
        var objPlane = new ZPlane(1, 1); // one empty strip: clears the room's mask there
        var obj = new RoomObject { X = 8, Y = 1, Width = 8, Height = 1 };
        var composed = ZPlanes.Compose(new[] { room, room }, 2, 32, 2, new[] { (obj, new[] { objPlane, null }) });
        Assert.False(composed[0].IsSet(8, 1));  // plane 1: the object's empty strip
        Assert.True(composed[0].IsSet(8, 0));   // above the object: the room's
        Assert.True(composed[1].IsSet(8, 1));   // plane 2: the object has none, the room's stays
        Assert.True(room.IsSet(8, 1));          // the room's own planes aren't changed
    }
}

/// <summary>The review marks file the room viewer writes and tools/mroom reads.</summary>
public class ReviewMarksTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mmucs-review-" + Guid.NewGuid().ToString("N"));

    public ReviewMarksTests() => MMUCS.Core.ReviewMarks.FilePath = System.IO.Path.Combine(_dir, "review.json");

    public void Dispose()
    {
        MMUCS.Core.ReviewMarks.FilePath = null;
        if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true);
    }

    private static MMUCS.Core.ReviewTarget Chicken(int x) => new()
    {
        Kind = "costume", Costume = 68, Akos = "0x0173E1B4",
        Snapshot = new MMUCS.Core.ReviewSnapshot { Placed = true, X = x, Y = 155, Scale = 150 },
    };

    private static MMUCS.Core.ReviewMark Mark(string note, params MMUCS.Core.ReviewTarget[] targets) =>
        new() { Room = 14, Note = note, Targets = targets.ToList() };

    [Fact]
    public void Marks_are_saved_numbered_and_replaced_for_the_same_set_of_things()
    {
        var first = MMUCS.Core.ReviewMarks.Add(Mark("too big", Chicken(36)));
        var pair = MMUCS.Core.ReviewMarks.Add(Mark("one of these is in front", Chicken(36), Chicken(160)));
        var again = MMUCS.Core.ReviewMarks.Add(Mark("too small", Chicken(36)));
        var pairAgain = MMUCS.Core.ReviewMarks.Add(Mark("the left one", Chicken(160), Chicken(36))); // order doesn't matter
        Assert.Equal((1, 2, 1, 2), (first.Id, pair.Id, again.Id, pairAgain.Id));
        var marks = MMUCS.Core.ReviewMarks.Load();
        Assert.Equal(2, marks.Count);
        Assert.Equal("too small", marks.Single(m => m.Id == 1).Note);
        Assert.Equal(2, marks.Single(m => m.Id == 2).Targets.Count);
        Assert.True(marks.Single(m => m.Id == 2).Includes(14, Chicken(36)));
        Assert.False(marks.Single(m => m.Id == 2).Includes(15, Chicken(36)));
        Assert.Contains("\"akos\": \"0x0173E1B4\"", System.IO.File.ReadAllText(MMUCS.Core.ReviewMarks.CurrentPath));
    }

    [Fact]
    public void Removing_a_mark_keeps_the_others()
    {
        MMUCS.Core.ReviewMarks.Add(Mark(null, Chicken(36)));
        MMUCS.Core.ReviewMarks.Add(Mark(null, Chicken(160)));
        Assert.True(MMUCS.Core.ReviewMarks.Remove(1));
        Assert.False(MMUCS.Core.ReviewMarks.Remove(1));
        Assert.Equal(2, Assert.Single(MMUCS.Core.ReviewMarks.Load()).Id);
    }

    [Fact]
    public void A_first_format_mark_is_read_as_one_target()
    {
        System.IO.Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(MMUCS.Core.ReviewMarks.CurrentPath, """
            {"format": 1, "marks": [{"id": 3, "room": 14, "kind": "object", "object": "fortbase-sign", "tags": ["order"],
              "snapshot": {"placed": true, "x": 8, "y": 16}}]}
            """);
        var mark = Assert.Single(MMUCS.Core.ReviewMarks.Load());
        var target = Assert.Single(mark.Targets);
        Assert.Equal(("object", "fortbase-sign", 8), (target.Kind, target.Object, target.Snapshot.X));
        Assert.Null(mark.LegacyKind);
    }
}

/// <summary>Where the engine puts an actor that follows the boxes: ScummVM's Actor::adjustXYToBeInBox.</summary>
public class PutInBoxTests
{
    private static Walkbox Box(int index, int left, int top, int right, int bottom, uint mask, uint flags = 0) =>
        new(index, new(left, top), new(right, top), new(right, bottom), new(left, bottom), mask, flags, 0, 255);

    private static readonly Walkbox Placeholder = new(0, new(-32000, -32000), new(-32000, -32000), new(-32000, -32000), new(-32000, -32000), 0, 0, 0, 255);

    [Fact]
    public void Of_two_boxes_holding_the_point_the_last_one_wins()
    {
        // cannon: boxes 1 (mask 1) and 11 (mask 0) both hold 116,424; the engine searches from the last box down
        var boxes = new List<Walkbox> { Placeholder, Box(1, 100, 400, 200, 440, 1), Box(2, 110, 410, 130, 430, 0) };
        Assert.Equal((116, 424, 2), RoomActors.PutInBox(boxes, 116, 424, isPlayer: false));
    }

    [Fact]
    public void Invisible_boxes_are_skipped_unless_player_only_for_another_actor()
    {
        var invisible = new List<Walkbox> { Placeholder, Box(1, 100, 400, 200, 440, 1), Box(2, 110, 410, 130, 430, 5, 0x80) };
        Assert.Equal(1, RoomActors.PutInBox(invisible, 116, 424, isPlayer: true).Box);
        var playerOnly = new List<Walkbox> { Placeholder, Box(1, 100, 400, 200, 440, 1), Box(2, 110, 410, 130, 430, 5, 0xA0) };
        Assert.Equal(2, RoomActors.PutInBox(playerOnly, 116, 424, isPlayer: false).Box);
        Assert.Equal(1, RoomActors.PutInBox(playerOnly, 116, 424, isPlayer: true).Box);
    }

    [Fact]
    public void A_point_outside_every_box_moves_to_the_closest_edge()
    {
        var boxes = new List<Walkbox> { Placeholder, Box(1, 100, 400, 200, 440, 1), Box(2, 300, 400, 400, 440, 2) };
        Assert.Equal((150, 400, 1), RoomActors.PutInBox(boxes, 150, 380, isPlayer: false));
        Assert.Equal((300, 420, 2), RoomActors.PutInBox(boxes, 280, 420, isPlayer: false));
    }

    [Fact]
    public void A_line_box_holds_the_points_on_it()
    {
        // town's walk paths: boxes whose corners pair up into a line (912,213 → 932,257)
        var line = new Walkbox(1, new(912, 213), new(912, 213), new(932, 257), new(932, 257), 1, 2, 0, 255);
        Assert.Equal((925, 241, 1), RoomActors.PutInBox(new List<Walkbox> { Placeholder, line }, 925, 241, isPlayer: false));
    }

    [Fact]
    public void With_no_boxes_the_actor_stays_where_it_is_put()
    {
        Assert.Equal((5, 6, 0), RoomActors.PutInBox(new List<Walkbox> { Placeholder }, 5, 6, isPlayer: false));
    }
}

public class PlacementTimingTests
{
    // A setup script: a top-level line, a line inside an if, and a line inside a cut-scene block.
    private static readonly ScriptLine[] Lines =
    {
        new(0, 0, "actor 18 costume 80"),
        new(10, 0, "put-actor 18 at 1460,0 in-room 15"),
        new(20, 0, "if (bit44) {"),
        new(30, 1, "put-actor 4 at 552,268 in-room current-room"),
        new(-1, 0, "}"),
        new(40, 0, "cut-scene(2) {"),
        new(50, 1, "if (local0) {"),
        new(60, 2, "put-actor 1 at 925,241 in-room current-room"),
        new(-1, 1, "}"),
        new(-1, 0, "}"),
    };

    [Theory]
    [InlineData(true, 12, PlacementTime.OnEntry)]     // top level of a script the room opens with
    [InlineData(true, 33, PlacementTime.OnEntryIf)]   // inside an if
    [InlineData(true, 64, PlacementTime.Cutscene)]    // inside a cut-scene, however deep
    [InlineData(false, 12, PlacementTime.Later)]      // a script the room doesn't open with
    [InlineData(false, 64, PlacementTime.Cutscene)]
    public void A_placement_runs_when_its_line_does(bool onEntry, int pos, PlacementTime expected)
        => Assert.Equal(expected, PlacementTiming.TimeAt(Lines, onEntry, pos));

    private static byte[] Code(params object[] parts)
    {
        var b = new List<byte>();
        foreach (var part in parts)
        {
            if (part is int v) { b.Add(0x01); b.AddRange(BitConverter.GetBytes(v)); }
            else if (part is byte[] raw) b.AddRange(raw);
        }
        return b.ToArray();
    }

    private static byte[] Op(byte op, int operand) { var b = new byte[5]; b[0] = op; BitConverter.GetBytes(operand).CopyTo(b, 1); return b; }
    private static readonly byte[] Costume = { 0xAC, 0x64 };
    private const int Bit5 = unchecked((int)0x80000005), CurrentRoom = 31;

    [Fact]
    public void On_a_first_visit_every_bit_is_0_and_the_room_is_known()
    {
        // if (bit5) { costume 1 } else { costume 2 }   (then-part 10-22, else part 22-29)
        var ifElse = Code(Op(0x02, Bit5), Op(0x65, 12), 1, Costume, Op(0x66, 7), 2, Costume);
        var branches = PlacementTiming.Branches(ifElse, 0, ifElse.Length, 11);
        Assert.False(PlacementTiming.Reached(branches, 12));
        Assert.True(PlacementTiming.Reached(branches, 24));
        // if (current-room == 11) { costume 1 }
        var room = Code(Op(0x02, CurrentRoom), 11, new byte[] { 0x08 }, Op(0x65, 7), 1, Costume);
        Assert.True(PlacementTiming.Reached(PlacementTiming.Branches(room, 0, room.Length, 11), 22));
        Assert.False(PlacementTiming.Reached(PlacementTiming.Branches(room, 0, room.Length, 29), 22));
        // if (var400) { costume 1 }: a global that isn't known holds nothing back
        var global = Code(Op(0x02, 400), Op(0x65, 7), 1, Costume);
        Assert.True(PlacementTiming.Reached(PlacementTiming.Branches(global, 0, global.Length, 11), 12));
    }

    [Theory]
    [InlineData(1u << 31, false)] // the lid has class 32: "is [32]" fails, so the else part runs (pistols)
    [InlineData(0u, true)]
    public void A_class_test_without_0x80_asks_that_the_object_lacks_the_class(uint lidClasses, bool thenRuns)
    {
        // if (class-of(650) is [32]) { costume 1 } else { costume 2 }   (then-part 21-33, else 33-40)
        var code = Code(650, 32, 1, new byte[] { 0xD0 }, Op(0x65, 12), 1, Costume, Op(0x66, 7), 2, Costume);
        var branches = PlacementTiming.Branches(code, 0, code.Length, 24, id => id == 650 ? lidClasses : null);
        Assert.Equal(thenRuns, PlacementTiming.Surely(branches, 23));
        Assert.Equal(!thenRuns, PlacementTiming.Surely(branches, 35));
    }
}

public class WalkboxScaleTests
{
    private static Walkbox Box(uint slot, uint scale) => new(1, new(0, 0), new(10, 0), new(10, 10), new(0, 10), 0, 0, slot, scale);

    [Fact]
    public void A_box_scales_by_its_own_scale_or_by_its_slot_interpolated_by_y()
    {
        var slots = new[] { new ScaleSlot(50, 100, 250, 300) };
        Assert.Equal(12, Walkboxes.ScaleAt(new[] { Box(0, 0), Box(0, 12) }, slots, 1, 5, 5));
        Assert.Equal(150, Walkboxes.ScaleAt(new[] { Box(0, 0), Box(1, 255) }, slots, 1, 5, 200));
        Assert.Equal(250, Walkboxes.ScaleAt(new[] { Box(0, 0), Box(1, 255) }, slots, 1, 5, 300));
        Assert.Equal(255, Walkboxes.ScaleAt(new[] { Box(0, 0), Box(1, 255) }, slots, 1, 5, 400)); // clipped to 255
        Assert.Equal(255, Walkboxes.ScaleAt(new[] { Box(0, 0), Box(1, 255) }, slots, 5, 5, 5));   // no such box
    }
}
