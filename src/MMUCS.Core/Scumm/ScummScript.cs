using System;
using System.Collections.Generic;
using System.Text;

namespace MMUCS.Core.Scumm;

/// <summary>One decoded SCUMM v8 bytecode instruction.</summary>
public sealed class ScriptInstruction
{
    /// <summary>Offset within the script's data (the block's DataSpan).</summary>
    public int Offset { get; init; }
    public int Length { get; init; }
    public byte Opcode { get; init; }
    /// <summary>Sub-op byte for grouped opcodes (actorOps, wait, print…), otherwise -1.</summary>
    public int SubOp { get; init; } = -1;
    /// <summary>ScummVM-style name, e.g. <c>pushWord</c> or <c>actorOps.costume</c>.</summary>
    public string Name { get; init; }
    /// <summary>Word immediate (a value, or a variable / array number), when <see cref="HasOperand"/>.</summary>
    public int Operand { get; init; }
    public bool HasOperand { get; init; }
    /// <summary>Absolute jump target (within the data), or -1.</summary>
    public int Target { get; init; } = -1;
    /// <summary>Inline string (a message or file name), already made readable.</summary>
    public string Text { get; init; }
    /// <summary>Not a known opcode / sub-op: decoding stops here.</summary>
    public bool Unknown { get; init; }

    internal ScummScript.OpDef Def { get; init; }
}

/// <summary>
/// SCUMM v8 (The Curse of Monkey Island) bytecode: the opcode table and a disassembler.
/// The table follows ScummVM's script_v8.cpp. In v8 a "word" is 32 bits: pushed values,
/// variable numbers and jump offsets are all int32. Each entry also says what the
/// instruction takes from the stack (in push order) and how <see cref="ScummDecompiler"/>
/// writes it.
/// </summary>
public static class ScummScript
{
    internal enum Imm : byte { None, Word, Var, Jump, String }

    /// <summary>
    /// <para><c>Args</c>: stack arguments in push order: <c>a</c> a value, <c>o</c> an object
    /// (written by name when known), <c>L</c> a list (count on top).</para>
    /// <para><c>Scumm</c>: how the decompiler writes it; <c>{0}</c>… are the arguments,
    /// <c>{s}</c> the inline string, <c>{v}</c> the variable / array.</para>
    /// </summary>
    internal sealed class OpDef
    {
        public string Name;
        public Imm Imm;
        public string Args = "";
        public bool Push;
        public string Scumm;
        /// <summary>Grouped opcodes: a sub-op byte follows, then <see cref="AfterSub"/>.</summary>
        public Dictionary<byte, OpDef> Subs;
        public Imm AfterSub;
    }

    internal static readonly OpDef[] Ops = new OpDef[256];

    private static OpDef Op(byte code, string name, string args = "", string scumm = null, Imm imm = Imm.None, bool push = false)
        => Ops[code] = new OpDef { Name = name, Args = args, Scumm = scumm, Imm = imm, Push = push };

    private static OpDef Group(byte code, string name, Imm afterSub = Imm.None, string args = "")
        => Ops[code] = new OpDef { Name = name, Subs = new(), AfterSub = afterSub, Args = args };

    private static void Sub(OpDef g, byte sub, string name, string args = "", string scumm = null, Imm imm = Imm.None)
        => g.Subs[sub] = new OpDef { Name = $"{g.Name}.{name}", Args = args, Scumm = scumm, Imm = imm };

    static ScummScript()
    {
        // ── Stack and expressions (written by the decompiler itself) ──
        Op(0x01, "pushWord", imm: Imm.Word);
        Op(0x02, "pushWordVar", imm: Imm.Var);
        Op(0x03, "wordArrayRead", "a", imm: Imm.Var, push: true);
        Op(0x04, "wordArrayIndexedRead", "aa", imm: Imm.Var, push: true);
        Op(0x05, "dup");
        Op(0x06, "pop", "a");
        Op(0x07, "not", "a", push: true);
        Op(0x08, "eq", "aa", push: true);
        Op(0x09, "neq", "aa", push: true);
        Op(0x0a, "gt", "aa", push: true);
        Op(0x0b, "lt", "aa", push: true);
        Op(0x0c, "le", "aa", push: true);
        Op(0x0d, "ge", "aa", push: true);
        Op(0x0e, "add", "aa", push: true);
        Op(0x0f, "sub", "aa", push: true);
        Op(0x10, "mul", "aa", push: true);
        Op(0x11, "div", "aa", push: true);
        Op(0x12, "land", "aa", push: true);
        Op(0x13, "lor", "aa", push: true);
        Op(0x14, "band", "aa", push: true);
        Op(0x15, "bor", "aa", push: true);
        Op(0x16, "mod", "aa", push: true);

        // ── Flow ──
        Op(0x64, "if", "a", imm: Imm.Jump);
        Op(0x65, "ifNot", "a", imm: Imm.Jump);
        Op(0x66, "jump", imm: Imm.Jump);
        Op(0x67, "breakHere", scumm: "break-here");
        Op(0x68, "delayFrames", "a", "break-here {0}");
        var wait = Group(0x69, "wait");
        Sub(wait, 0x1E, "forActor", "a", "wait-for-actor {0}", Imm.Jump);
        Sub(wait, 0x1F, "forMessage", scumm: "wait-for-message");
        Sub(wait, 0x20, "forCamera", scumm: "wait-for-camera");
        Sub(wait, 0x21, "forSentence", scumm: "wait-for-sentence");
        Sub(wait, 0x22, "forAnimation", "a", "wait-for-animation {0}", Imm.Jump);
        Sub(wait, 0x23, "forTurn", "a", "wait-for-turn {0}", Imm.Jump);
        Op(0x6a, "delay", "a", "sleep-for {0} jiffies");
        Op(0x6b, "delaySeconds", "a", "sleep-for {0} seconds");
        Op(0x6c, "delayMinutes", "a", "sleep-for {0} minutes");

        // ── Variables and arrays ──
        Op(0x6d, "writeWordVar", "a", "{v} = {0}", Imm.Var);
        Op(0x6e, "wordVarInc", "", "{v}++", Imm.Var);
        Op(0x6f, "wordVarDec", "", "{v}--", Imm.Var);
        var dim = Group(0x70, "dimArray", Imm.Var);
        Sub(dim, 0x0A, "int", "a", "dim int {v}[{0}]");
        Sub(dim, 0x0B, "string", "a", "dim string {v}[{0}]");
        Sub(dim, 0x0C, "undim", "", "undim {v}");
        Op(0x71, "wordArrayWrite", "aa", "{v}[{0}] = {1}", Imm.Var);
        Op(0x72, "wordArrayInc", "a", "{v}[{0}]++", Imm.Var);
        Op(0x73, "wordArrayDec", "a", "{v}[{0}]--", Imm.Var);
        var dim2 = Group(0x74, "dim2dimArray", Imm.Var);
        Sub(dim2, 0x0A, "int", "aa", "dim int {v}[{0}][{1}]");
        Sub(dim2, 0x0B, "string", "aa", "dim string {v}[{0}][{1}]");
        Sub(dim2, 0x0C, "undim", "", "undim {v}");
        Op(0x75, "wordArrayIndexedWrite", "aaa", "{v}[{0}][{1}] = {2}", Imm.Var);
        var arr = Group(0x76, "arrayOps", Imm.Var);
        Sub(arr, 0x14, "assignString", "a", "{v}[{0}] = {s}", Imm.String);
        Sub(arr, 0x15, "assignIntList", "La", "{v}[{1}] = {0}");
        Sub(arr, 0x16, "assign2dimList", "aLa", "{v}[{0}][{2}] = {1}");

        // ── Scripts ──
        Op(0x79, "startScript", "aaL");
        Op(0x7a, "startScriptQuick", "aL");
        Op(0x7b, "stopObjectCode", scumm: "end-script");
        Op(0x7c, "stopScript", "a", "stop-script {0}");
        Op(0x7d, "jumpToScript", "aaL");
        Op(0x7e, "dummy", scumm: "dummy");
        Op(0x7f, "startObject", "aoaL");
        Op(0x80, "stopObjectScript", "o", "stop-object {0}");
        Op(0x81, "cutscene", "L");
        Op(0x82, "endCutscene");
        Op(0x83, "freezeUnfreeze", "a", "freeze-scripts {0}");
        Op(0x84, "beginOverride");
        Op(0x85, "endOverride", scumm: "end-override");
        Op(0x86, "stopSentence", scumm: "stop-sentence");
        // Not in ScummVM's table; COMI uses it after debug prints with one value (0) pushed.
        Op(0x87, "debug", "a", "debug {0}");

        // ── Objects, camera ──
        Op(0x89, "setClass", "oL", "class-of {0} is {1}");
        Op(0x8a, "setState", "oa", "state-of {0} is {1}");
        Op(0x8b, "setOwner", "oa", "owner-of {0} is {1}");
        Op(0x8c, "panCameraTo", "aa", "pan-camera-to {0},{1}");
        Op(0x8d, "actorFollowCamera", "a", "camera-follow {0}");
        Op(0x8e, "setCameraAt", "aa", "camera-at {0},{1}");

        // ── Text: print ops are one instruction per sub-op ──
        foreach (var (code, name) in new (byte, string)[]
                 {
                     (0x8f, "printActor"), (0x90, "printEgo"), (0x93, "printLine"), (0x94, "printText"),
                     (0x95, "printDebug"), (0x96, "printSystem"), (0x97, "blastText"),
                 })
        {
            var g = Group(code, name);
            Sub(g, 0xC8, "begin", code == 0x8f ? "a" : "");
            Sub(g, 0xC9, "end");
            Sub(g, 0xCA, "at", "aa", "at {0},{1}");
            Sub(g, 0xCB, "color", "a", "color {0}");
            Sub(g, 0xCC, "center", scumm: "center");
            Sub(g, 0xCD, "charset", "a", "charset {0}");
            Sub(g, 0xCE, "left", scumm: "left");
            Sub(g, 0xCF, "overhead", scumm: "overhead");
            Sub(g, 0xD0, "mumble", scumm: "mumble");
            Sub(g, 0xD1, "string", "", "{s}", Imm.String);
            Sub(g, 0xD2, "wrap", scumm: "wrap");
        }
        Op(0x91, "talkActor", "a", "say-line {0} {s}", Imm.String);
        Op(0x92, "talkEgo", "", "say-line {s}", Imm.String);
        Op(0x98, "drawObject", "oaaa", "draw-object {0} at {1},{2} state {3}");

        var cursor = Group(0x9c, "cursorCommand");
        Sub(cursor, 0xDC, "cursorOn", scumm: "cursor on");
        Sub(cursor, 0xDD, "cursorOff", scumm: "cursor off");
        Sub(cursor, 0xDE, "cursorSoftOn", scumm: "cursor soft-on");
        Sub(cursor, 0xDF, "cursorSoftOff", scumm: "cursor soft-off");
        Sub(cursor, 0xE0, "userputOn", scumm: "userput on");
        Sub(cursor, 0xE1, "userputOff", scumm: "userput off");
        Sub(cursor, 0xE2, "userputSoftOn", scumm: "userput soft-on");
        Sub(cursor, 0xE3, "userputSoftOff", scumm: "userput soft-off");
        Sub(cursor, 0xE4, "image", "oa", "cursor image {0} frame {1}");
        Sub(cursor, 0xE5, "hotspot", "aa", "cursor hotspot {0},{1}");
        Sub(cursor, 0xE6, "transparent", "a", "cursor transparent {0}");
        Sub(cursor, 0xE7, "charset", "a", "charset {0}");
        Sub(cursor, 0xE8, "charsetColors", "L", "charset colors {0}");
        Sub(cursor, 0xE9, "put", "aa", "cursor put {0},{1}");

        // ── Rooms and actors ──
        Op(0x9d, "loadRoom", "a", "current-room {0}");
        Op(0x9e, "loadRoomWithEgo", "oaa", "come-out-door {0} at {1},{2}");
        Op(0x9f, "walkActorToObj", "aoa", "walk-actor {0} to-object {1} within {2}");
        Op(0xa0, "walkActorTo", "aaa", "walk-actor {0} to {1},{2}");
        Op(0xa1, "putActorAtXY", "aaaa", "put-actor {0} at {1},{2} in-room {3}");
        Op(0xa2, "putActorAtObject", "ao", "put-actor {0} at-object {1}");
        Op(0xa3, "faceActor", "ao", "face-actor {0} towards {1}");
        Op(0xa4, "animateActor", "aa", "do-animation {0} {1}");
        Op(0xa5, "doSentence", "aoo", "do-sentence {0} {1} with {2}");
        Op(0xa6, "pickupObject", "o", "pick-up-object {0}");
        Op(0xa7, "setBoxFlags", "La", "set-box {0} to {1}");
        Op(0xa8, "createBoxMatrix", scumm: "set-box-path");

        var res = Group(0xaa, "resourceRoutines");
        string[] resKinds = { "charset", "costume", "object", "room", "script", "sound" };
        for (int i = 0; i < 6; i++) Sub(res, (byte)(0x3C + i), $"load{Cap(resKinds[i])}", "a", $"load-{resKinds[i]} {{0}}");
        string[] lockKinds = { "costume", "room", "script", "sound" };
        for (int i = 0; i < 4; i++)
        {
            Sub(res, (byte)(0x42 + i), $"lock{Cap(lockKinds[i])}", "a", $"lock-{lockKinds[i]} {{0}}");
            Sub(res, (byte)(0x46 + i), $"unlock{Cap(lockKinds[i])}", "a", $"unlock-{lockKinds[i]} {{0}}");
            Sub(res, (byte)(0x4A + i), $"nuke{Cap(lockKinds[i])}", "a", $"nuke-{lockKinds[i]} {{0}}");
        }

        var room = Group(0xab, "roomOps");
        Sub(room, 0x52, "palette", "aaaa", "palette {3} is rgb {0},{1},{2}");
        Sub(room, 0x57, "fade", "a", "fade {0}");
        Sub(room, 0x58, "rgbIntensity", "aaaaa", "palette-intensity {0},{1},{2} slots {3} to {4}");
        Sub(room, 0x59, "transform", "aaaa", "palette-transform {0} slots {1} to {2} time {3}");
        Sub(room, 0x5C, "newPalette", "a", "palette {0}");
        Sub(room, 0x5D, "saveGame", scumm: "save-game");
        Sub(room, 0x5E, "loadGame", scumm: "load-game");
        Sub(room, 0x5F, "saturation", "aaaaa", "palette-saturation {0},{1},{2} slots {3} to {4}");

        // actorOps / verbOps write "actor N …" / "verb N …"; the decompiler keeps the current one.
        var actor = Group(0xac, "actorOps");
        Sub(actor, 0x64, "costume", "a", "costume {0}");
        Sub(actor, 0x65, "stepDist", "aa", "step-dist {0},{1}");
        Sub(actor, 0x67, "animationDefault", scumm: "animation-default");
        Sub(actor, 0x68, "initAnimation", "a", "init-animation {0}");
        Sub(actor, 0x69, "talkAnimation", "aa", "talk-animation {0},{1}");
        Sub(actor, 0x6A, "walkAnimation", "a", "walk-animation {0}");
        Sub(actor, 0x6B, "standAnimation", "a", "stand-animation {0}");
        Sub(actor, 0x6C, "animationSpeed", "a", "animation-speed {0}");
        Sub(actor, 0x6D, "default", scumm: "default");
        Sub(actor, 0x6E, "elevation", "a", "elevation {0}");
        Sub(actor, 0x6F, "palette", "aa", "palette {0} is {1}");
        Sub(actor, 0x70, "talkColor", "a", "talk-color {0}");
        Sub(actor, 0x71, "name", "", "name {s}", Imm.String);
        Sub(actor, 0x72, "width", "a", "width {0}");
        Sub(actor, 0x73, "scale", "a", "scale {0}");
        Sub(actor, 0x74, "neverZClip", scumm: "never-zclip");
        Sub(actor, 0x75, "alwaysZClip", "a", "always-zclip {0}");
        Sub(actor, 0x76, "ignoreBoxes", scumm: "ignore-boxes");
        Sub(actor, 0x77, "followBoxes", scumm: "follow-boxes");
        Sub(actor, 0x78, "specialDraw", "a", "special-draw {0}");
        Sub(actor, 0x79, "textOffset", "aa", "text-offset {0},{1}");
        Sub(actor, 0x7A, "setCurrent", "a");
        Sub(actor, 0x7B, "variable", "aa", "variable {0} is {1}");
        Sub(actor, 0x7C, "ignoreTurnsOn", scumm: "ignore-turns on");
        Sub(actor, 0x7D, "ignoreTurnsOff", scumm: "ignore-turns off");
        Sub(actor, 0x7E, "new", scumm: "new");
        Sub(actor, 0x7F, "depth", "a", "depth {0}");
        Sub(actor, 0x80, "stop", scumm: "stop");
        Sub(actor, 0x81, "face", "a", "face {0}");
        Sub(actor, 0x82, "turn", "a", "turn {0}");
        Sub(actor, 0x83, "walkScript", "a", "walk-script {0}");
        Sub(actor, 0x84, "talkScript", "a", "talk-script {0}");
        Sub(actor, 0x85, "walkPause", scumm: "walk-pause");
        Sub(actor, 0x86, "walkResume", scumm: "walk-resume");
        Sub(actor, 0x87, "volume", "a", "volume {0}");
        Sub(actor, 0x88, "frequency", "a", "frequency {0}");
        Sub(actor, 0x89, "pan", "a", "pan {0}");

        var camera = Group(0xad, "cameraOps");
        Sub(camera, 0x32, "pause", scumm: "camera-pause");
        Sub(camera, 0x33, "resume", scumm: "camera-resume");

        var verb = Group(0xae, "verbOps");
        Sub(verb, 0x96, "setCurrent", "a");
        Sub(verb, 0x97, "new", scumm: "new");
        Sub(verb, 0x98, "delete", scumm: "delete");
        Sub(verb, 0x99, "name", "", "name {s}", Imm.String);
        Sub(verb, 0x9A, "at", "aa", "at {0},{1}");
        Sub(verb, 0x9B, "on", scumm: "on");
        Sub(verb, 0x9C, "off", scumm: "off");
        Sub(verb, 0x9D, "color", "a", "color {0}");
        Sub(verb, 0x9E, "hiColor", "a", "hicolor {0}");
        Sub(verb, 0xA0, "dimColor", "a", "dimcolor {0}");
        Sub(verb, 0xA1, "dim", scumm: "dim");
        Sub(verb, 0xA2, "key", "a", "key {0}");
        Sub(verb, 0xA3, "image", "oa", "image {0} in-room {1}");
        Sub(verb, 0xA4, "nameString", "a", "name-string {0}");
        Sub(verb, 0xA5, "center", scumm: "center");
        Sub(verb, 0xA6, "charset", "a", "charset {0}");
        Sub(verb, 0xA7, "lineSpacing", "a", "line-spacing {0}");

        // ── Sound, system ──
        Op(0xaf, "startSound", "a", "start-sound {0}");
        Op(0xb0, "startMusic", "a", "start-music {0}");
        Op(0xb1, "stopSound", "a", "stop-sound {0}");
        Op(0xb2, "soundKludge", "L", "sound-kludge {0}");
        var system = Group(0xb3, "systemOps");
        Sub(system, 0x28, "restart", scumm: "restart");
        Sub(system, 0x29, "quit", scumm: "quit");
        // The arguments are popped before the sub-op is read.
        var verbs = Group(0xb4, "saveRestoreVerbs", args: "aaa");
        Sub(verbs, 0x8D, "save", scumm: "save-verbs {0} to {1} set {2}");
        Sub(verbs, 0x8E, "restore", scumm: "restore-verbs {0} to {1} set {2}");
        Sub(verbs, 0x8F, "delete", scumm: "delete-verbs {0} to {1} set {2}");
        Op(0xb5, "setObjectName", "o", "name-of {0} is {s}", Imm.String);
        Op(0xb6, "getDateTime", scumm: "get-date-time");
        Op(0xb7, "drawBox", "aaaaa", "draw-box {0},{1} to {2},{3} color {4}");
        Op(0xb9, "startVideo", "", "start-video {s}", Imm.String);
        Op(0xba, "kernelSetFunctions", "L");

        // Unlike ScummVM's startScriptQuick2, COMI always stores what this returns.
        Op(0xc8, "startScriptQuick2", "aL", push: true);
        Op(0xc9, "startObjectQuick", "oaL");

        // ── Functions (push a value) ──
        Op(0xca, "pickOneOf", "aL", "pick-one-of({0}, {1})", push: true);
        Op(0xcb, "pickOneOfDefault", "aLa", "pick-one-of({0}, {1}, default {2})", push: true);
        Op(0xcd, "isAnyOf", "aL", "{0} in {1}", push: true);
        Op(0xce, "getRandomNumber", "a", "random({0})", push: true);
        Op(0xcf, "getRandomNumberRange", "aa", "random-between({0}, {1})", push: true);
        Op(0xd0, "ifClassOfIs", "oL", "class-of({0}) is {1}", push: true);
        Op(0xd1, "getState", "o", "state-of({0})", push: true);
        Op(0xd2, "getOwner", "o", "owner-of({0})", push: true);
        Op(0xd3, "isScriptRunning", "a", "script-running({0})", push: true);
        Op(0xd5, "isSoundRunning", "a", "sound-running({0})", push: true);
        Op(0xd6, "abs", "a", "abs({0})", push: true);
        Op(0xd8, "kernelGetFunctions", "L", push: true);
        Op(0xd9, "isActorInBox", "aa", "actor-in-box({0}, {1})", push: true);
        Op(0xda, "getVerbEntrypoint", "oa", "valid-verb({0}, {1})", push: true);
        Op(0xdb, "getActorFromXY", "aa", "actor-at({0}, {1})", push: true);
        Op(0xdc, "findObject", "aa", "object-at({0}, {1})", push: true);
        Op(0xdd, "getVerbFromXY", "aa", "verb-at({0}, {1})", push: true);
        Op(0xdf, "findInventory", "aa", "find-inventory({0}, {1})", push: true);
        Op(0xe0, "getInventoryCount", "a", "inventory-count({0})", push: true);
        Op(0xe1, "getAnimateVariable", "aa", "actor-variable({0}, {1})", push: true);
        Op(0xe2, "getActorRoom", "a", "actor-room({0})", push: true);
        Op(0xe3, "getActorWalkBox", "a", "actor-box({0})", push: true);
        Op(0xe4, "getActorMoving", "a", "actor-moving({0})", push: true);
        Op(0xe5, "getActorCostume", "a", "actor-costume({0})", push: true);
        Op(0xe6, "getActorScaleX", "a", "actor-scale({0})", push: true);
        Op(0xe7, "getActorLayer", "a", "actor-layer({0})", push: true);
        Op(0xe8, "getActorElevation", "a", "actor-elevation({0})", push: true);
        Op(0xe9, "getActorWidth", "a", "actor-width({0})", push: true);
        Op(0xea, "getObjectNewDir", "a", "actor-facing({0})", push: true);
        Op(0xeb, "getObjectX", "o", "x-of({0})", push: true);
        Op(0xec, "getObjectY", "o", "y-of({0})", push: true);
        Op(0xed, "getActorChore", "a", "actor-chore({0})", push: true);
        Op(0xee, "distObjectObject", "oo", "proximity({0}, {1})", push: true);
        Op(0xef, "distPtPt", "aaaa", "distance({0},{1}, {2},{3})", push: true);
        Op(0xf0, "getObjectImageX", "o", "image-x({0})", push: true);
        Op(0xf1, "getObjectImageY", "o", "image-y({0})", push: true);
        Op(0xf2, "getObjectImageWidth", "o", "image-width({0})", push: true);
        Op(0xf3, "getObjectImageHeight", "o", "image-height({0})", push: true);
        Op(0xf6, "getStringWidth", "a", "string-width({0}, {s})", Imm.String, push: true);
        Op(0xf7, "getActorZPlane", "a", "actor-zplane({0})", push: true);
    }

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Decodes <paramref name="code"/>[start, end). Stops at the first unknown opcode.</summary>
    public static List<ScriptInstruction> Disassemble(ReadOnlySpan<byte> code, int start = 0, int end = -1)
    {
        if (end < 0 || end > code.Length) end = code.Length;
        var list = new List<ScriptInstruction>();
        int pos = Math.Max(0, start);
        while (pos < end)
        {
            var ins = Decode(code, pos, end);
            list.Add(ins);
            if (ins.Unknown) break;
            pos += ins.Length;
        }
        return list;
    }

    private static ScriptInstruction Decode(ReadOnlySpan<byte> code, int at, int end)
    {
        int pos = at;
        byte opcode = code[pos++];
        var def = Ops[opcode];
        if (def == null)
            return new ScriptInstruction { Offset = at, Length = 1, Opcode = opcode, Name = $"unknown 0x{opcode:X2}", Unknown = true };

        int sub = -1;
        Imm imm = def.Imm;
        if (def.Subs != null)
        {
            if (pos >= end) return Truncated(at, end, opcode, def.Name);
            sub = code[pos++];
            if (!def.Subs.TryGetValue((byte)sub, out var sd))
                return new ScriptInstruction { Offset = at, Length = pos - at, Opcode = opcode, SubOp = sub, Name = $"{def.Name}.unknown 0x{sub:X2}", Unknown = true };
            // Grouped ops read their variable right after the sub-op; a sub-op's own immediate follows.
            if (def.AfterSub != Imm.None && !ReadImm(code, ref pos, end, def.AfterSub, out _, out _)) return Truncated(at, end, opcode, def.Name);
            int afterSubOperand = def.AfterSub != Imm.None ? code.S32LE(pos - 4) : 0;
            imm = sd.Imm;
            if (!ReadImm(code, ref pos, end, imm, out int v, out string text)) return Truncated(at, end, opcode, sd.Name);
            return new ScriptInstruction
            {
                Offset = at, Length = pos - at, Opcode = opcode, SubOp = sub, Name = sd.Name, Def = sd,
                HasOperand = def.AfterSub != Imm.None, Operand = afterSubOperand,
                Target = imm == Imm.Jump ? pos + v : -1, Text = text,
            };
        }

        if (!ReadImm(code, ref pos, end, imm, out int value, out string str)) return Truncated(at, end, opcode, def.Name);
        return new ScriptInstruction
        {
            Offset = at, Length = pos - at, Opcode = opcode, Name = def.Name, Def = def,
            HasOperand = imm is Imm.Word or Imm.Var, Operand = value,
            Target = imm == Imm.Jump ? pos + value : -1, Text = str,
        };
    }

    private static ScriptInstruction Truncated(int at, int end, byte opcode, string name)
        => new() { Offset = at, Length = end - at, Opcode = opcode, Name = $"{name} (truncated)", Unknown = true };

    private static bool ReadImm(ReadOnlySpan<byte> code, ref int pos, int end, Imm imm, out int value, out string text)
    {
        value = 0;
        text = null;
        switch (imm)
        {
            case Imm.None:
                return true;
            case Imm.Word:
            case Imm.Var:
            case Imm.Jump:
                if (pos + 4 > end) return false;
                value = code.S32LE(pos);
                pos += 4;
                return true;
            case Imm.String:
                int len = StringLength(code, pos, end);
                if (len < 0) return false;
                text = DecodeString(code.Slice(pos, len));
                pos += len + 1;
                return true;
        }
        return false;
    }

    /// <summary>
    /// Length of a 0-terminated message (ScummVM resStrLen): 0xFF/0xFE escapes take a code
    /// byte, and every code but 1, 2, 3 and 8 a 32-bit argument (in v8).
    /// </summary>
    private static int StringLength(ReadOnlySpan<byte> code, int pos, int end)
    {
        int p = pos;
        while (p < end)
        {
            byte c = code[p];
            if (c == 0) return p - pos;
            p++;
            if (c is 0xFF or 0xFE)
            {
                if (p >= end) return -1;
                byte esc = code[p++];
                if (esc is not (1 or 2 or 3 or 8)) p += 4;
            }
        }
        return -1;
    }

    /// <summary>A message as text: quoted, with escapes written as <c>\n</c>, <c>%i{var}</c> and so on.</summary>
    private static string DecodeString(ReadOnlySpan<byte> s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            byte c = s[i];
            if (c is 0xFF or 0xFE && i + 1 < s.Length)
            {
                byte esc = s[++i];
                if (esc is 1 or 2 or 3 or 8)
                {
                    sb.Append(esc switch { 1 => "\\n", 2 => "\\k", 3 => "\\w", _ => "\\8" });
                    continue;
                }
                int arg = i + 4 < s.Length ? s.S32LE(i + 1) : 0;
                i += 4;
                sb.Append(esc switch
                {
                    4 => $"%i{{{VarName(arg)}}}",
                    5 => $"%v{{{VarName(arg)}}}",
                    6 => $"%n{{{VarName(arg)}}}",
                    7 => $"%s{{{VarName(arg)}}}",
                    _ => $"\\x{esc:X2}{{{arg}}}",
                });
                continue;
            }
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c < 0x20) sb.Append($"\\x{c:X2}");
            else sb.Append((char)c); // Latin-1
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>v8 variable references: bit 31 marks a bit variable, bit 30 a local; the rest are globals.</summary>
    public static string VarName(int v)
    {
        uint u = (uint)v;
        if ((u & 0x80000000) != 0) return $"bit{u & 0x7FFFFFFF}";
        if ((u & 0x40000000) != 0) return $"local{u & 0x0FFFFFFF}";
        return $"var{u}";
    }

    /// <summary>A plain listing: offset, bytes, name and operand per line.</summary>
    public static string FormatListing(ReadOnlySpan<byte> code, IReadOnlyList<ScriptInstruction> list)
    {
        var sb = new StringBuilder(list.Count * 40);
        foreach (var ins in list)
        {
            sb.Append(ins.Offset.ToString("X4")).Append("  ");
            int n = Math.Min(ins.Length, 6);
            var hex = Convert.ToHexString(code.Slice(ins.Offset, n));
            sb.Append(hex.PadRight(12)).Append(ins.Length > 6 ? "… " : "  ");
            sb.Append(ins.Name);
            if (ins.HasOperand)
            {
                bool isVar = ins.Def?.Imm == Imm.Var || ins.SubOp >= 0;
                sb.Append(' ').Append(isVar && ins.Opcode != 0x01 ? VarName(ins.Operand) : ins.Operand.ToString());
            }
            if (ins.Target >= 0) sb.Append(" → ").Append(ins.Target.ToString("X4"));
            if (ins.Text != null) sb.Append(' ').Append(ins.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
