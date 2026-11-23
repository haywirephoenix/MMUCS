using MMUCS.Benchmarks;

// Timing and memory checks against the real game, for performance work (see notes/performance.md):
//
//   dotnet run -c Release --project tests/MMUCS.Benchmarks -- <benchmark|all> [path/to/COMI.LA0]
//
// The game comes from the second argument or the MMUCS_GAME environment variable. Run in Release:
// Debug builds time very differently. "all" runs every benchmark except audio (it plays sound).

var benchmarks = new (string Name, string About, Action Run)[]
{
    ("cutscene", "decoding and converting cutscene frames; frames freed once replaced", Cutscene.Run),
    ("rooms", "what a room switch costs, step by step; single presses and a held key", RoomSwitching.Run),
    ("presses", "how often rooms go black at press speeds from a held key to slow presses", RoomSwitching.RunPressSpeeds),
    ("slow", "single switches on a simulated slow machine: covered after 100 ms, then whole", RoomSwitching.RunSlow),
    ("memory", "room images freed as rooms change, and peak memory", RoomMemory.Run),
    ("library", "room 3 (an image library): opening, lazy icons, toggles over Full-screen objects", RoomMemory.RunLibrary),
    ("tree", "the hierarchy: per-row work, and unfolding a large room with and without colours", Hierarchy.Run),
    ("audio", "the sound device: reading the play position, clock against real time (plays sound)", Audio.Run),
};

string name = args.Length > 0 ? args[0] : null;
string game = args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("MMUCS_GAME");
var chosen = name == "all" ? benchmarks.Where(b => b.Name != "audio").ToArray() : benchmarks.Where(b => b.Name == name).ToArray();
if (chosen.Length == 0 || string.IsNullOrEmpty(game) || !File.Exists(game))
{
    Console.WriteLine("usage: MMUCS.Benchmarks <benchmark|all> [path/to/COMI.LA0]   (or set MMUCS_GAME)");
    foreach (var b in benchmarks) Console.WriteLine($"  {b.Name,-9} {b.About}");
    return string.IsNullOrEmpty(name) ? 0 : 1;
}

Host.GameFile = game;
foreach (var b in chosen)
{
    Console.WriteLine($"── {b.Name}: {b.About}");
    b.Run();
    Console.WriteLine();
}
return 0;
