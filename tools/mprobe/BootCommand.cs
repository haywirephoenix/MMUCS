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
        bool calls = a.Contains("--calls"), everyFrame = a.Contains("--every-frame");

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
            : $"ROOM {room}{(store.RoomSize(room) is { } size ? $" ({size.Width}×{size.Height})" : "")}", vm.Depth);
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
        for (int i = 0; i < jiffies; i++) loop.Tick();
        if (seen.Count == 0) Console.WriteLine("  nothing started, ended, entered or faulted (the running scripts just went on)");
        var repeats = seen.Where(kv => kv.Value > 1).OrderByDescending(kv => kv.Value).ToList();
        if (repeats.Count > 0)
        {
            Console.WriteLine("  repeated:");
            foreach (var (text, count) in repeats.Take(20)) Console.WriteLine($"    ×{count,-5} {text}");
        }

        Console.WriteLine();
        Console.WriteLine($"after {loop.Jiffies} jiffies, {loop.Frames} frames: room {vm.CurrentRoom}, stack {state.Stack.Count}");
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
        return faults > 0 ? 1 : 0;
    }

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
