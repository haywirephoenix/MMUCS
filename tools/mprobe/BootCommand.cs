using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;

/// <summary>
/// mprobe boot: runs the game's scripts headless with MMUCS.Runtime's interpreter, from the boot script
/// on, and prints what happens: scripts starting and ending (indented by nesting), rooms entered, faults
/// and warnings, then the scripts still running, chosen variables and the instructions not carried out yet.
/// </summary>
static class BootCommand
{
    public static int Run(ScummBlock root, List<string> a, Func<List<string>, string, string> takeOption, string gameFile = null)
    {
        double seconds = double.TryParse(takeOption(a, "--seconds"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 10;
        int bootParam = int.TryParse(takeOption(a, "--boot") ?? takeOption(a, "--param"), out var p) ? p : 0;
        string vars = takeOption(a, "--vars");
        string png = takeOption(a, "--png");
        var watchLimbs = (takeOption(a, "--limbs") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        var lastLimbs = new Dictionary<int, string>();
        bool calls = a.Contains("--calls"), everyFrame = a.Contains("--every-frame");
        var inputs = new List<(int Jiffy, string What, string Spec)>();
        for (string c; (c = takeOption(a, "--click")) != null;) inputs.Add(Timed(c, "click"));
        for (string c; (c = takeOption(a, "--right-click")) != null;) inputs.Add(Timed(c, "right-click"));
        for (string c; (c = takeOption(a, "--hold")) != null;) inputs.Add(Timed(c, "hold"));
        for (string c; (c = takeOption(a, "--move")) != null;) inputs.Add(Timed(c, "move"));
        for (string k; (k = takeOption(a, "--key")) != null;) inputs.Add(Timed(k, "key"));
        for (string f; (f = takeOption(a, "--save")) != null;) inputs.Add(Timed(f, "save"));
        string loadFile = takeOption(a, "--load");
        string savesDir = takeOption(a, "--saves");

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
        // The sound effects (read along with the game's time, so the scripts see them end) and the music the
        // scripts ask for (its tables are in comi.exe), followed but not heard.
        var effects = new MMUCS.Runtime.Sound.SoundEffects(MMUCS.Runtime.Sound.SoundEffects.FromResources(store, root));
        effects.Started += id => Line($"SOUND {id} starts", vm.Depth);
        vm.Effects = effects;
        var voice = new MMUCS.Runtime.Sound.VoicePlayer(MMUCS.Runtime.Sound.VoicePlayer.FromBundles(gameFile));
        vm.Speech = voice;
        if (gameFile != null) MMUCS.Runtime.ScriptedGame.UseFonts(vm, MMUCS.Core.Fonts.NutFont.LoadAll(gameFile));
        vm.Said += (text, tag, actor) => Line($"SAY {(actor == 0xFF ? "line" : $"actor {actor}")}{(tag.Length > 0 ? $" {tag}" : "")}: {text.Replace('\n', ' ')}", vm.Depth);
        vm.SystemMessage += text => Line($"SYSTEM {text}", vm.Depth);
        MMUCS.Runtime.Sound.ComiMusic music = null;
        MMUCS.Runtime.Sound.SilentMusic musicOutput = null;
        if (MMUCS.Core.Audio.MusicTable.Load(gameFile) is { } table)
        {
            music = new MMUCS.Runtime.Sound.ComiMusic(table, musicOutput = new MMUCS.Runtime.Sound.SilentMusic());
            music.Changed += what => Line($"MUSIC {what}", vm.Depth);
        }
        else Console.WriteLine("no comi.exe beside the game: the music isn't followed");
        vm.SoundKludge = MMUCS.Runtime.ScriptedGame.SoundKludge(effects, voice, music, musicOutput);
        var sounds = MMUCS.Runtime.ScriptedGame.FollowSaves(loop, effects, voice, music);
        // The F1 menu's save slots and registry settings: a folder given, or none (empty slots, the registry's defaults).
        if (savesDir != null)
        {
            new MMUCS.Runtime.GameSaves(Path.GetFullPath(savesDir)).Attach(loop, sounds, effects, voice, music);
            var write = vm.SaveGameWrite;
            vm.SaveGameWrite = (slot, name) =>
            {
                bool ok = write(slot, name);
                Line($"SAVE slot {slot} \"{name}\"{(ok ? "" : " failed")}", vm.Depth);
                return ok;
            };
            var loadSlot = loop.LoadSlot;
            loop.LoadSlot = slot =>
            {
                bool ok = loadSlot(slot);
                Line(ok ? $"LOAD slot {slot}: room {vm.CurrentRoom}, {state.Slots.Count(x => x.IsAlive)} scripts running" : $"LOAD slot {slot} failed", vm.Depth);
                return ok;
            };
            var writeRegistry = vm.WriteRegistry;
            vm.WriteRegistry = (name, value) =>
            {
                writeRegistry(name, value);
                Line($"REGISTRY {name} = {value}", vm.Depth);
            };
        }
        loop.AfterScripts += jiffies =>
        {
            effects.Update(jiffies);
            voice.Update(jiffies);
            music?.Refresh();
        };
        bool quit = false;
        vm.QuitRequested += () => { quit = true; Line("QUIT (the run ends after this frame)", vm.Depth); };
        loop.StateSaved += () => Line($"SAVE temporary (game-loaded {state[EngineVars.GameLoaded]})", vm.Depth);
        loop.StateLoaded += () => Line($"LOAD temporary: room {vm.CurrentRoom}, {state.Slots.Count(x => x.IsAlive)} scripts running", vm.Depth);
        vm.Faulted += f => { faults++; Line($"FAULT {f}", vm.Depth); };
        vm.Warned += w => { warnings++; Line($"warning: {w}", vm.Depth); };
        if (calls) vm.Unhandled += c => Line($"· {c.Instruction.Name}({string.Join(", ", c.Args.Select(x => x.Length == 1 ? x[0].ToString() : $"[{string.Join(", ", x)}]"))}){(c.Instruction.Text is { } t ? " " + t : "")}", vm.Depth);

        vm.Boot(bootParam);
        booting = false;
        if (loadFile != null)
        {
            // A save file loaded over the booted game, as the game window's F4 does.
            var missing = new List<string>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (var file = File.OpenRead(loadFile))
                MMUCS.Runtime.ScriptedGame.ReadSave(file, loop, effects, voice, music, missing);
            Line($"LOAD {loadFile}: room {vm.CurrentRoom}, {state.Slots.Count(x => x.IsAlive)} scripts running ({clock.ElapsedMilliseconds} ms)");
            foreach (var what in missing.Distinct()) Line($"  missing: {what}");
        }

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
                if (input.What == "save")
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    MMUCS.Runtime.Scripts.StateFile.Summary summary;
                    using (var file = File.Create(input.Spec))
                        summary = MMUCS.Runtime.ScriptedGame.WriteSave(file, vm, music, voice, effects);
                    Line($"SAVE {input.Spec}: room {vm.CurrentRoom}, {summary.Objects} objects, {summary.Bytes / 1024} KiB ({clock.ElapsedMilliseconds} ms)");
                    Console.WriteLine("  most objects: " + string.Join(", ", summary.ObjectsByType.Take(12).Select(x => $"{x.Key} ×{x.Value}")));
                    Console.WriteLine("  the engine's own, by type: " + string.Join(", ", summary.Services.Select(x => $"{x.Key} ×{x.Value}")));
                    continue;
                }
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
                if (input.What == "move")
                {
                    Line($"MOVE {point.X},{point.Y}");
                    continue;
                }
                int under = vm.FindObject(point.X + loop.ScreenOrigin.X, point.Y + loop.ScreenOrigin.Y);
                Line($"{input.What.ToUpperInvariant()} {point.X},{point.Y} on {(under == 0 ? "nothing" : $"object {under} {RoomScripts.ObjectName(root, under)}")}");
                var button = input.What == "right-click" ? MouseButton.Right : MouseButton.Left;
                loop.Press(button);
                int held = input.What == "hold" ? 60 : 6; // a hold lasts a second (the verb coin opens meanwhile)
                if (button == MouseButton.Left) releaseLeft = i + held; else releaseRight = i + held;
            }
            if (i == releaseLeft) loop.Release(MouseButton.Left);
            if (i == releaseRight) loop.Release(MouseButton.Right);
            loop.Tick();
            if (quit) break;
            foreach (int n in watchLimbs)
            {
                // Each change of the actor's limbs (frame, position in the sequence), with the speech's mouth.
                if (vm.Actors.Find(n) is not { } act) continue;
                string now = $"anim {act.Frame}  " + string.Join(" ", Enumerable.Range(0, Actor.Limbs).Where(l => act.Active[l] != 0)
                    .Select(l => $"{l}:{act.CurPos[l]}{((act.Stopped >> l & 1) != 0 ? "s" : "")}"));
                if (lastLimbs.GetValueOrDefault(n) == now) continue;
                lastLimbs[n] = now;
                var (w, h) = voice.LipSync(state[EngineVars.Sync] + 50);
                string talk = state[EngineVars.TalkActor] == n ? $"  talking, mouth {w}×{h}{(voice.Playing is { } tag ? $" in {tag}" : "")}" : "";
                Console.WriteLine($"{loop.Jiffies / 60.0,7:0.000} s  f{loop.Frames,-4} LIMBS actor {n} {now}{talk}");
            }
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
                          $"userput {vm.UserPutLevel}, cursor {vm.CursorLevel} (object {vm.CursorImage.Object} image {vm.CursorImage.Image}), " +
                          $"camera {vm.CameraX},{vm.CameraY}{(vm.CameraFollows != 0 ? $" following actor {vm.CameraFollows}" : "")}{(vm.CameraFrozen ? " (paused)" : "")}");
        if (vm.RoomObjects.Count > 0)
        {
            Console.WriteLine("room objects (what --click OBJECT aims at):");
            foreach (var o in vm.RoomObjects)
                Console.WriteLine($"  {o.Id,5} {RoomScripts.ObjectName(root, o.Id),-36} {o.X},{o.Y} {o.Width}×{o.Height}" +
                                  $"  stand {Stand(o)}" +
                                  $"{(state.Objects.HasClass(o.Id, ObjectTables.Untouchable) ? "  untouchable" : "")}{(o.Parent != 0 ? $"  parent slot {o.Parent} in state {o.ParentState}" : "")}");
        }
        string Stand(ObjectPlace o)
        {
            var (sx, sy, dir) = o.StandAt(state.Objects.IsObject(o.Id) ? state.Objects.State(o.Id) : 0);
            return $"{sx},{sy} facing {dir} ({o.ActorDir}°)";
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
                                  $" layer {act.Layer}{(act.Visible ? "" : " hidden")}{(act.Moving != 0 ? $" moving {act.Moving}" : "")}  {drawn}  limbs {limbs}" +
                                  $"{(act.ShadowMode != 0 ? $"  special-draw {act.ShadowMode}" : "")}{CostumeColours(vm, act)}");
            }
        }
        PrintPalette(vm);
        if (vm.BlastObjects.Count > 0)
        {
            Console.WriteLine("objects blasted over the room (last frame):");
            foreach (var b in vm.BlastObjects)
                Console.WriteLine($"  {b.Object,5} {RoomScripts.ObjectName(root, b.Object),-36} image {b.Image} at {b.X},{b.Y} {b.Width}×{b.Height}" +
                                  $"{(b.ScaleX != 255 || b.ScaleY != 255 ? $" scale {b.ScaleX}/{b.ScaleY}" : "")}{(b.Mode == 3 ? " shadow" : "")}{(vm.IsFloating(b.Object) ? " (floating)" : "")}");
        }
        var shownVerbs = vm.VerbsShown.ToList();
        if (shownVerbs.Count > 0)
        {
            Console.WriteLine("verbs on the screen (dialogue choices…):");
            foreach (var (verb, text, (l, t, r, b)) in shownVerbs) Console.WriteLine($"  verb {verb,-4} {l},{t}–{r},{b}  {text.Replace('\n', '|')}");
        }
        if (vm.ScreenTexts.Count > 0)
        {
            Console.WriteLine("texts on the screen (as the last frame drew them):");
            foreach (var t in vm.ScreenTexts)
                Console.WriteLine($"  {t.X,4},{t.Y,-4} charset {t.Charset} color {t.Color,-3}{(t.Center ? " centred" : "")}{(t.Wrap ? " wrapped" : "")}  {t.Text.Replace('\n', '|')}");
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
        if (png != null) SaveRoom(vm, png, gameFile);
        return faults > 0 ? 1 : 0;
    }

    /// <summary>
    /// The screen's colours against the room's palette in use: the slots the scripts changed, in runs that
    /// share a scale (what <c>palette-intensity</c> gave them; <c>set</c> where a colour isn't a scaled room colour).
    /// </summary>
    private static void PrintPalette(ScriptVm vm)
    {
        if (vm.CurrentRoom == 0) return;
        var palettes = vm.RoomPalettes(vm.CurrentRoom);
        if (palettes.Count == 0) return;
        var room = palettes[Math.Clamp(vm.PaletteIndex, 0, palettes.Count - 1)];
        var cur = vm.Palette.Current;
        // The scales (per channel, as a range) that give a slot's colour from the room's: now = was * scale / 255,
        // at most 255. Null when the colour is the room's; empty when no scale gives it (a colour set outright).
        (int Lo, int Hi)[] ScalesOf(int i)
        {
            if (i * 3 + 2 >= room.Length) return [];
            if (cur.AsSpan(i * 3, 3).SequenceEqual(room.AsSpan(i * 3, 3))) return null;
            var r = new (int, int)[3];
            for (int c = 0; c < 3; c++)
            {
                int was = room[i * 3 + c], now = cur[i * 3 + c];
                if (was == 0) { if (now != 0) return []; r[c] = (0, 999); continue; }
                int lo = (now * 255 + was - 1) / was, hi = now == 255 ? 999 : ((now + 1) * 255 + was - 1) / was - 1;
                if (lo > hi) return [];
                r[c] = (lo, hi);
            }
            return r;
        }
        var runs = new List<(int From, int To, (int Lo, int Hi)[] Scales)>();
        for (int i = 0; i < ScreenPalette.Colours; i++)
        {
            var scales = ScalesOf(i);
            if (scales == null) continue;
            if (runs.Count > 0 && runs[^1].To == i - 1 && runs[^1].Scales.Length == 3 && scales.Length == 3)
            {
                var both = runs[^1].Scales.Zip(scales, (x, y) => (Math.Max(x.Lo, y.Lo), Math.Min(x.Hi, y.Hi))).ToArray();
                if (both.All(x => x.Item1 <= x.Item2)) { runs[^1] = (runs[^1].From, i, both); continue; }
            }
            else if (runs.Count > 0 && runs[^1].To == i - 1 && runs[^1].Scales.Length == 0 && scales.Length == 0)
            {
                runs[^1] = (runs[^1].From, i, scales);
                continue;
            }
            runs.Add((i, i, scales));
        }
        static string Scale((int Lo, int Hi)[] s) =>
            s.Length == 0 ? "set" : string.Join(",", s.Select(x => x.Hi >= 999 ? (x.Lo == 0 ? "any" : $"{x.Lo}+") : x.Lo == x.Hi ? $"{x.Lo}" : $"{x.Lo}–{x.Hi}"));
        Console.WriteLine($"palette: room {vm.CurrentRoom}'s palette {vm.PaletteIndex}{(vm.Palette.IsTransforming ? ", transforming" : "")}" +
                          (runs.Count == 0 ? ", as the room has it" : ", changed by the scripts (slots: the intensity r,g,b of 255 that gives them, or set):"));
        foreach (var (from, to, scales) in runs.Take(24))
            Console.WriteLine($"  {(from == to ? $"{from}" : $"{from}–{to}"),-8} {Scale(scales)}");
        if (runs.Count > 24) Console.WriteLine($"  … {runs.Count - 24} more runs");
    }

    /// <summary>The screen colours an actor's costume draws in (its AKPL through the actor's own), as ranges, shadow colours (1–7) left out.</summary>
    private static string CostumeColours(ScriptVm vm, Actor a)
    {
        if (vm.Scripts.Costume(a.Costume) is not { } costume) return "";
        var akpl = costume.Data.Palette.Span;
        if (akpl.IsEmpty || costume.Data.IsPassthroughPalette) return "  colours: the palette's own";
        var remap = ActorPicture.RemapOf(a.Palette, akpl.Length);
        var used = new SortedSet<int>();
        for (int i = 1; i < akpl.Length; i++)
            if (AkosFrames.Global((byte)i, akpl, false, remap) is var g and >= 8) used.Add(g);
        var ranges = new List<string>();
        foreach (int g in used)
            if (ranges.Count > 0 && ranges[^1].Split('–') is var r && int.Parse(r[^1]) == g - 1) ranges[^1] = $"{r[0]}–{g}";
            else ranges.Add(g.ToString());
        return $"  colours {string.Join(",", ranges)}{(remap != null ? " (remapped)" : "")}";
    }

    /// <summary>
    /// The current room as the game window draws it: the objects the engine draws in their states, and the
    /// actors as the last frame drew them, at the room's full size.
    /// </summary>
    private static void SaveRoom(ScriptVm vm, string file, string gameFile)
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
        var screen = vm.Palette.Snapshot();
        var scene = MMUCS.Player.Rooms.RoomSceneBuilder.BuildIndexedAsync(model, StateOf).GetAwaiter().GetResult().Colour(screen);
        scene = scene.WithActors(MMUCS.Player.Rooms.RoomSceneBuilder.SceneActors(vm.Actors.Pictures, scene.ZMasks.Count, screen: screen));
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(Math.Max(1, scene.Width), Math.Max(1, scene.Height)));
        surface.Canvas.Clear(SkiaSharp.SKColors.Black);
        scene.Draw(surface.Canvas, new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Nearest));
        // The blasted objects on the page, then the texts, as the game window draws them.
        var page = new SkiaSharp.SKPoint(vm.CameraX - 320, vm.CameraY - 240);
        foreach (var b in vm.BlastObjects)
        {
            if (MMUCS.Player.ScriptedView.BlastImage(vm, b) is not { } rgba) continue;
            var image = MMUCS.Player.SkiaImages.Of(rgba);
            surface.Canvas.DrawImage(image, SkiaSharp.SKRect.Create(page.X + b.X, page.Y + b.Y, rgba.Width * b.ScaleX / 255f, rgba.Height * b.ScaleY / 255f));
        }
        // The actors over the blasts (layer below 0), cut by the room's planes as the others.
        if (vm.Actors.UpperPictures.Count > 0)
            new MMUCS.Player.Rooms.RoomScene { Width = scene.Width, Height = scene.Height, ZMasks = scene.ZMasks,
                Actors = MMUCS.Player.Rooms.RoomSceneBuilder.SceneActors(vm.Actors.UpperPictures, scene.ZMasks.Count, screen: screen) }
                .Draw(surface.Canvas, new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Nearest));
        // The texts on the page, in the game fonts and the screen's colours, as the game window draws them.
        int drawnTexts = 0;
        if (gameFile != null && vm.ScreenTexts.Count > 0)
        {
            var text = MMUCS.Player.Text.SubtitleRenderer.Create(MMUCS.Core.Fonts.NutFont.LoadAll(gameFile), MMUCS.Player.Text.SubtitleFontKind.Game, null, true);
            var palette = screen;
            surface.Canvas.Save();
            surface.Canvas.Translate(vm.CameraX - 320, vm.CameraY - 240);
            foreach (var t in vm.ScreenTexts)
            {
                var flags = (t.Center ? MMUCS.Core.Smush.SmushTextFlags.Center : 0) | (t.Wrap ? MMUCS.Core.Smush.SmushTextFlags.Wrap : 0);
                using var overlay = text.BuildText(new MMUCS.Core.Smush.SmushText { X = t.X, Y = t.Y, Flags = flags, Font = t.Charset, Color = t.Color, Text = t.Text }, palette, 640, 480);
                if (overlay == null) continue;
                overlay.Draw(surface.Canvas, smooth: false);
                drawnTexts++;
            }
            surface.Canvas.Restore();
        }
        using var data = surface.Snapshot().Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is { } dir) Directory.CreateDirectory(dir);
        File.WriteAllBytes(file, data.ToArray());
        Console.WriteLine($"saved room {vm.CurrentRoom} with {scene.Objects.Count} objects, {scene.Actors.Count + vm.Actors.UpperPictures.Count} actors and {drawnTexts} texts to {Path.GetFullPath(file)}");
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
