using System;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// One of the engine's actors (ScummVM <c>Actor</c>, v8): where it is and in which room, the costume it
/// wears and the state of that costume's limbs, how it's drawn, and the numbers the scripts set and read.
/// The engine's rules for changing it live in <see cref="ActorTable"/>; this holds the state, so saving
/// it is a matter of copying fields.
/// </summary>
public sealed class Actor
{
    /// <summary>How many limbs a costume drives and how many animation variables an actor has (ScummVM).</summary>
    public const int Limbs = 16, AnimVars = 27;

    /// <summary>A limb's frame when nothing is decoded into it (ScummVM 0xFFFF).</summary>
    public const int NoFrame = 0xFFFF;

    /// <summary>ScummVM's <c>MF_*</c> bits in <see cref="Moving"/>.</summary>
    public const int NewLeg = 1, InLeg = 2, Turning = 4, LastLeg = 8, Frozen = 0x80;

    /// <summary>The box an actor that ignores the boxes is in (ScummVM <c>kInvalidBox</c>).</summary>
    public const int InvalidBox = 0xFF;

    /// <summary><see cref="ForceClip"/>'s "use the walk box's z-plane" (v8 scripts say 255 for it).</summary>
    public const int BoxClip = 100;

    public Actor(int number)
    {
        Number = number;
        Init(-1);
    }

    public int Number { get; }

    public int Costume { get; set; }
    public int Room { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Elevation { get; set; }

    /// <summary>Degrees: 0 north (away), 90 east, 180 south (towards the camera), 270 west.</summary>
    public int Facing { get; set; }
    public int TargetFacing { get; set; }

    public bool Visible { get; set; }
    public bool NeedRedraw { get; set; }
    public bool CostumeNeedsInit { get; set; }

    /// <summary>The animation last started (ScummVM <c>_frame</c>: init, walk, stand… or any other).</summary>
    public int Frame { get; set; }

    public int Width { get; set; }
    public int TalkColor { get; set; }
    public int TalkX { get; set; }
    public int TalkY { get; set; }
    public int ScaleX { get; set; }
    public int ScaleY { get; set; }
    public int BoxScale { get; set; }
    public int Layer { get; set; }
    public int ShadowMode { get; set; }

    /// <summary>The z-plane it's drawn behind: 0 none, <see cref="BoxClip"/> its walk box's (0–255, a byte in the engine).</summary>
    public int ForceClip { get; set; }

    public bool IgnoreBoxes { get; set; }
    public bool IgnoreTurns { get; set; }

    /// <summary>Set by the costume (AKOS flip): draws the other way round.</summary>
    public bool Flip { get; set; }

    public int Walkbox { get; set; }
    public int Moving { get; set; }
    public int SpeedX { get; set; }
    public int SpeedY { get; set; }

    /// <summary>Frames between costume steps (0 or 1: every frame) and the count towards the next.</summary>
    public int AnimSpeed { get; set; }
    public int AnimProgress { get; set; }

    public int InitFrame { get; set; }
    public int WalkFrame { get; set; }
    public int StandFrame { get; set; }
    public int TalkStartFrame { get; set; }
    public int TalkStopFrame { get; set; }

    public int WalkScript { get; set; }
    public int TalkScript { get; set; }
    public int TalkVolume { get; set; }
    public int TalkFrequency { get; set; }
    public int TalkPan { get; set; }

    public string Name { get; set; }

    public int[] Vars { get; } = new int[AnimVars];
    public int[] Palette { get; } = new int[256];

    // ── The costume's limbs (ScummVM CostumeData) ──
    public byte[] Active { get; } = new byte[Limbs];
    public int[] LimbFrame { get; } = new int[Limbs];
    public int[] Start { get; } = new int[Limbs];
    public int[] End { get; } = new int[Limbs];
    public int[] CurPos { get; } = new int[Limbs];

    /// <summary>Limbs that are stopped (bit <i>n</i>: limb <i>n</i>); they keep their place but aren't drawn.</summary>
    public int Stopped { get; set; }

    /// <summary>Where it was last drawn, in room pixels (for <c>actor-at</c>), or null when it wasn't.</summary>
    public (int Left, int Top, int Right, int Bottom)? Drawn { get; set; }

    public bool IsMoving => Moving != 0;

    /// <summary>ScummVM <c>CostumeData::reset</c>: every limb idle with nothing decoded.</summary>
    public void ResetCostume()
    {
        Stopped = 0;
        for (int i = 0; i < Limbs; i++)
        {
            Active[i] = 0;
            CurPos[i] = Start[i] = End[i] = LimbFrame[i] = NoFrame;
        }
    }

    /// <summary>
    /// ScummVM <c>Actor::initActor</c> (v8): −1 sets everything up as at the start, 1 also takes it out of
    /// its room and its costume (<c>default</c>), 2 just faces it forward (<c>new</c>); every mode resets
    /// how it's drawn, its talk settings and its animations.
    /// </summary>
    public void Init(int mode)
    {
        if (mode == -1)
        {
            NeedRedraw = false;
            CostumeNeedsInit = false;
            Visible = false;
            Flip = false;
            Frame = 0;
            Walkbox = 0;
            AnimProgress = 0;
            Array.Clear(Vars);
            Array.Clear(Palette);
            ResetCostume();
            WalkScript = 0;
        }
        if (mode is 1 or -1)
        {
            Costume = 0;
            Room = 0;
            X = Y = 0;
            Facing = 180;
            Visible = false;
        }
        else if (mode == 2)
            Facing = 180;

        Elevation = 0;
        Width = 24;
        TalkColor = 15;
        TalkX = 0;
        TalkY = -80;
        BoxScale = ScaleY = ScaleX = 0xFF;
        TargetFacing = Facing;
        ShadowMode = 0;
        Layer = 0;
        Moving = 0;
        SpeedX = 8;
        SpeedY = 2;
        AnimSpeed = 0;
        AnimProgress = 0;
        IgnoreBoxes = false;
        ForceClip = BoxClip;
        IgnoreTurns = false;
        TalkFrequency = 256;
        TalkPan = 64;
        TalkVolume = 127;
        InitFrame = 1;
        WalkFrame = 2;
        StandFrame = 3;
        TalkStartFrame = 4;
        TalkStopFrame = 5;
        WalkScript = 0;
        TalkScript = 0;
    }

    public override string ToString() => $"actor {Number}";
}
