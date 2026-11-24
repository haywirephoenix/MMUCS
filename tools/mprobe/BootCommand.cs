using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;

/// <summary>
/// mprobe boot: runs the game's scripts headless with MMUCS.Runtime's interpreter, from the boot script
/// on, and prints what happens: scripts starting and ending (indented by nesting), rooms entered, faults
/// and warnings, then the scripts still running, chosen variables and the instructions not carried out yet.
/// </summary>
static class BootCommand
{
    public static int Run(ScummBlock root, List<string> a, Func<List<string>, string, string> takeOption)
    {
        double seconds = double.TryParse(takeOption(a, "--seconds"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 10;
        int bootParam = int.TryParse(takeOption(a, "--param"), out var p) ? p : 0;
        string vars = takeOption(a, "--vars");
        string png = takeOption(a, "--png");
        bool calls = a.Contains("--calls"), everyFrame = a.Contains("--every-frame");
        var inputs = new List<(int Jiffy, string What, string Spec)>();
        for (string c; (c = takeOption(a, "--click")) != null;) inputs.Add(Timed(c, "click"));
        for (string c; (c = takeOption(a, "--right-click")) != null;) inputs.Add(Timed(c, "right-click"));
        for (string k; (k = takeOption(a, "--key")) != null;) inputs.Add(Timed(k, "key"));

        var limits = GameLimits.Read(root) ?? throw new InvalidOperationException("no MAXS block: not a v8 game index");
        var store = new ScriptStore(root);
        var vm = new ScriptVm(store, new VmState(limits));
        var loop = new FrameLoop(vm);
        var state = vm.State;
        int faults = 0, warnings = 0;
        bool booting = true;
        var seen = new Dictionary<string, int>(); // during frames: each distinct line once, then counted

        Console.WriteLine($"limits: {limits.Variables} variables, {limits.BitVariables} bits, {limits.GlobalObjects} objects, " +
                          $"{limits.IndexArrays.Count} index arrays; {store.GlobalCount - 1} global script numbers, {store.RoomNumbers.Count()} rooms");
        Console.WriteLine();
        Console.WriteLine($"boot (global 1, boot param {bootParam}):");

        void Line(string text, int depth = 0)
        {
            if (!booting && !everyFrame)
            {
                seen[text] = seen.GetValueOrDefault(text) + 1;
                if (seen[text] > 1) return;
            }
            string when = $"{loop.Jiffies / 60.0,7:0.000} s  f{loop.Frames,-4}";
            Console.WriteLine($"{when} {new string(' ', depth * 2)}{text}");
        }

        vm.ScriptStarted += slot => Line($"▸ {What(slot)}", vm.Depth - 1);
        vm.ScriptEnded += (slot, why) => Line($"◂ {What(slot)} {why}", Math.Max(0, vm.Depth - 1));
        vm.RoomEntered += room => Line(room == 0
            ? "ROOM 0 (no room)"
            : $"ROOM {room} {store.RoomName(room)}{(store.RoomSize(room) is { } size ? $" ({size.Width}×{size.Height})" : "")}", vm.Depth);
        vm.VideoStarted += name => Line($"VIDEO {name} (not played here: the script goes straight on)", vm.Depth);
        vm.Faulted += f => { faults++; Line($"FAULT {f}", vm.Depth); };
        vm.Warned += w => { warnings++; Line($"warning: {w}", vm.Depth); };
        if (calls) vm.Unhandled += c => Line($"· {c.Instruction.Name}({string.Join(", ", c.Args.Select(x => x.Length == 1 ? x[0].ToString() : $"[{string.Join(", ", x)}]"))}){(c.Instruction.Text is { } t ? " " + t : "")}", vm.Depth);

        vm.Boot(bootParam);
        booting = false;

        Console.WriteLine();
        int next = state[EngineVars.TimerNext];
        Console.WriteLine($"after boot: room {vm.CurrentRoom}, timer-next {next} (a frame every {Math.Max(1, next)} jiffies, {60.0 / Math.Max(1, next):0.#} a second), " +
                          $"{state.Slots.Count(x => x.IsAlive)} scripts running, stack {state.Stack.Count}");

        int jiffies = (int)Math.Round(seconds * 60);
        Console.WriteLine();
        Console.WriteLine($"{seconds:0.##} s of frames ({jiffies} jiffies){(everyFrame ? "" : ", each line shown the first time")}:");
        int releaseLeft = -1, releaseRight = -1;
        for (int i = 0; i < jiffies; i++)
        {
            foreach (var input in inputs.Where(x => x.Jiffy == i))
            {
                if (input.What == "key")
                {
                    int code = Key(input.Spec);
                    Line($"KEY {input.Spec} ({code})");
                    loop.PressKey(code);
                    continue;
                }
                if (Point(input.Spec, vm, root) is not { } point)
                {
                    Line($"CLICK {input.Spec}: no such object in room {vm.CurrentRoom}");
                    continue;
                }
                loop.Mouse = point;
                int under = vm.FindObject(point.X + loop.ScreenOrigin.X, point.Y + loop.ScreenOrigin.Y);
                Line($"{input.What.ToUpperInvariant()} {point.X},{point.Y} on {(under == 0 ? "nothing" : $"object {under} {RoomScripts.ObjectName(root, under)}")}");
                var button = input.What == "click" ? MouseButton.Left : MouseButton.Right;
                loop.Press(button);
                if (button == MouseButton.Left) releaseLeft = i + 6; else releaseRight = i + 6;
            }
            if (i == releaseLeft) loop.Release(MouseButton.Left);
            if (i == releaseRight) loop.Release(MouseButton.Right);
            loop.Tick();
        }
        if (seen.Count == 0) Console.WriteLine("  nothing started, ended, entered or faulted (the running scripts just went on)");
        var repeats = seen.Where(kv => kv.Value > 1).OrderByDescending(kv => kv.Value).ToList();
        if (repeats.Count > 0)
        {
            Console.WriteLine("  repeated:");
            foreach (var (text, count) in repeats.Take(20)) Console.WriteLine($"    ×{count,-5} {text}");
        }

        Console.WriteLine();
        Console.WriteLine($"after {loop.Jiffies} jiffies, {loop.Frames} frames: room {vm.CurrentRoom} {store.RoomName(vm.CurrentRoom)}, stack {state.Stack.Count}, " +
                          $"userput {vm.UserPutLevel}, cursor {vm.CursorLevel} (object {vm.CursorImage.Object} image {vm.CursorImage.Image})");
        if (vm.RoomObjects.Count > 0)
        {
            Console.WriteLine("room objects (what --click OBJECT aims at):");
            foreach (var o in vm.RoomObjects)
                Console.WriteLine($"  {o.Id,5} {RoomScripts.ObjectName(root, o.Id),-36} {o.X},{o.Y} {o.Width}×{o.Height}" +
                                  $"{(state.Objects.HasClass(o.Id, ObjectTables.Untouchable) ? "  untouchable" : "")}{(o.Parent != 0 ? $"  parent slot {o.Parent} in state {o.ParentState}" : "")}");
        }
        var actors = vm.Actors.All.Where(x => x.Number > 0 && x.Room == vm.CurrentRoom && vm.CurrentRoom != 0).ToList();
        if (actors.Count > 0)
        {
            Console.WriteLine("actors in the room (what --click actorN aims at):");
            foreach (var act in actors)
            {
                string limbs = string.Join(" ", Enumerable.Range(0, Actor.Limbs).Where(i => act.Active[i] != 0).Select(i => $"{i}:{act.CurPos[i]}{((act.Stopped >> i & 1) != 0 ? "s" : "")}"));
                string drawn = act.Drawn is var (l, t, r, b) ? $"drawn {l},{t}–{r},{b}" : "not drawn";
                Console.WriteLine($"  actor {act.Number,-2} {(act.Name is { Length: > 0 } n ? n : ""),-12} costume {act.Costume,-4} at {act.X},{act.Y}" +
                                  $"{(act.Elevation != 0 ? $" up {act.Elevation}" : "")} facing {act.Facing,-3} anim {act.Frame,-3} box {act.Walkbox,-3} scale {act.ScaleX,-3}" +
                                  $" layer {act.Layer}{(act.Visible ? "" : " hidden")}{(act.Moving != 0 ? $" moving {act.Moving}" : "")}  {drawn}  limbs {limbs}");
            }
        }
        Console.WriteLine("running:");
        foreach (var slot in state.Slots.Where(x => x.IsAlive))
        {
            string status = slot.Status == SlotStatus.Paused ? $"sleeping, {slot.Delay} jiffies left" : "running";
            if (slot.Frozen) status += ", frozen";
            Console.WriteLine($"  slot {slot.Index,-2} {What(slot),-40} {status}, at {slot.Pc}");
        }

        Console.WriteLine("variables:");
        var shown = new List<int> { EngineVars.Room, EngineVars.TimerNext, EngineVars.CurrentDisk, EngineVars.Ego, EngineVars.SentenceScript, 55, 303, 304 };
        var bits = new List<int>();
        foreach (string v in (vars ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (v.StartsWith("bit") && int.TryParse(v[3..], out int bit) && bit >= 0 && bit < limits.BitVariables) bits.Add(bit);
            else if (Variable(v, limits.Variables) is int n) shown.Add(n);
            else Console.WriteLine($"  {v}: no such variable");
        foreach (int n in shown.Distinct())
            Console.WriteLine($"  var{n,-4} {VariableNames.Global(n) ?? "",-24} = {state[n]}");
        foreach (int bit in bits.Distinct())
            Console.WriteLine($"  bit{bit,-4} {VariableNames.Bit(bit) ?? "",-24} = {(state.Bit(bit) ? 1 : 0)}");

        Console.WriteLine("not carried out yet (instruction × times met):");
        foreach (var line in vm.UnhandledCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ×{kv.Value}").Chunk(4))
            Console.WriteLine("  " + string.Join("   ", line));

        Console.WriteLine();
        Console.WriteLine($"{faults} faults, {warnings} warnings");
        if (png != null) SaveRoom(vm, png);
        return faults > 0 ? 1 : 0;
    }

    /// <summary>
    /// The current room as the game window draws it: the objects the engine draws in their states, and the
    /// actors as the last frame drew them, at the room's full size.
    /// </summary>
    private static void SaveRoom(ScriptVm vm, string file)
    {
        if (vm.CurrentRoom == 0 || vm.Scripts.RoomBlock(vm.CurrentRoom) is not { } lflf || !RoomModel.TryCreate(lflf, out var model))
        {
            Console.WriteLine($"no room to save to {file}");
            return;
        }
        var drawn = new Dictionary<int, int>();
        foreach (var o in vm.RoomObjects)
            if (vm.IsDrawn(o.Id)) drawn[o.Id] = vm.LocalState(o.Id);
        int StateOf(RoomObject o) => drawn.GetValueOrDefault(vm.Scripts.ObjectId(o.Name));
        var scene = MMUCS.Player.Rooms.RoomSceneBuilder.BuildAsync(model, StateOf).GetAwaiter().GetResult();
        scene = scene.WithActors(MMUCS.Player.Rooms.RoomSceneBuilder.SceneActors(vm.Actors.Pictures, scene.ZMasks.Count));
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(Math.Max(1, scene.Width), Math.Max(1, scene.Height)));
        surface.Canvas.Clear(SkiaSharp.SKColors.Black);
        scene.Draw(surface.Canvas, new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Nearest));
        using var data = surface.Snapshot().Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is { } dir) Directory.CreateDirectory(dir);
        File.WriteAllBytes(file, data.ToArray());
        Console.WriteLine($"saved room {vm.CurrentRoom} with {scene.Objects.Count} objects and {scene.Actors.Count} actors to {Path.GetFullPath(file)}");
    }

    /// <summary>"WHAT@SECONDS" (seconds after boot, default 1) as a jiffy.</summary>
    private static (int, string, string) Timed(string spec, string what)
    {
        var parts = spec.Split('@');
        double at = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 1;
        return ((int)Math.Round(at * 60), what, parts[0]);
    }

    /// <summary>A click target: "X,Y" on the screen, an object (number or name) of the current room at its middle, or "actorN" at the middle of where it was drawn.</summary>
    private static (int X, int Y)? Point(string spec, ScriptVm vm, ScummBlock root)
    {
        var xy = spec.Split(',');
        if (xy.Length == 2 && int.TryParse(xy[0], out int x) && int.TryParse(xy[1], out int y)) return (x, y);
        if (spec.StartsWith("actor") && int.TryParse(spec[5..], out int n) && vm.Actors.Find(n)?.Drawn is var (l, t, r, b))
            return ((l + r) / 2, (t + b) / 2);
        foreach (var o in vm.RoomObjects)
            if (o.Id.ToString() == spec || RoomScripts.ObjectName(root, o.Id) == spec)
                return (o.X + o.Width / 2, o.Y + o.Height / 2);
        return null;
    }

    /// <summary>A key: one character, "esc", "enter", "space", "f1"…"f9", or a number.</summary>
    private static int Key(string spec) => spec.ToLowerInvariant() switch
    {
        "esc" or "escape" => FrameLoop.EscapeKey,
        "enter" or "return" => 13,
        "space" => 32,
        var f when f.Length >= 2 && f[0] == 'f' && int.TryParse(f[1..], out int n) && n is >= 1 and <= 9 => FrameLoop.F1Key + n - 1,
        var c when c.Length == 1 => spec[0],
        _ => int.TryParse(spec, out int code) ? code : 0,
    };

    private static string What(ScriptSlot slot) => slot.Code?.ToString() ?? $"{slot.Kind} {slot.Number}";

    /// <summary>A variable by number or by name (as Core's VariableNames has it).</summary>
    private static int? Variable(string v, int count)
    {
        if (int.TryParse(v.StartsWith("var") ? v[3..] : v, out int n)) return n >= 0 && n < count ? n : null;
        for (int i = 0; i < count; i++)
            if (VariableNames.Global(i) == v) return i;
        return null;
    }
}
