using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static MMUCS.Core.Scumm.ScummScript;

namespace MMUCS.Core.Scumm;

/// <summary>One line of decompiled SCUMM.</summary>
public readonly record struct ScriptLine(int Offset, int Indent, string Text);

/// <summary>
/// Turns v8 bytecode back into readable SCUMM-like source: the stack is run symbolically so
/// pushes become arguments and expressions, then the jumps are rebuilt into if / else / while
/// blocks. Jumps that don't nest are left as <c>goto</c> with a label. One pass over the code,
/// so it is fast enough to run on every selection.
/// </summary>
public static class ScummDecompiler
{
    // ── Expressions ──

    private sealed class Expr
    {
        public string Text;
        public int Prec;       // binding strength: higher binds tighter
        public int Start;      // offset of the first instruction that built it
        public bool Call;      // has side effects (a function call): kept when discarded
        public int? Literal;
        public string Op;      // comparison operator, for clean negation
        public Expr Left, Right;
    }

    private const int PAtom = 10, PUnary = 9, PMul = 8, PAdd = 7, PCmp = 6, PEq = 5, PBand = 4, PBor = 3, PLand = 2, PLor = 1;

    private static readonly Dictionary<byte, (string Op, int Prec)> Binary = new()
    {
        [0x08] = ("==", PEq), [0x09] = ("!=", PEq), [0x0a] = (">", PCmp), [0x0b] = ("<", PCmp),
        [0x0c] = ("<=", PCmp), [0x0d] = (">=", PCmp), [0x0e] = ("+", PAdd), [0x0f] = ("-", PAdd),
        [0x10] = ("*", PMul), [0x11] = ("/", PMul), [0x12] = ("&&", PLand), [0x13] = ("||", PLor),
        [0x14] = ("&", PBand), [0x15] = ("|", PBor), [0x16] = ("%", PMul),
    };

    private static readonly Dictionary<string, string> Inverse = new()
    {
        ["=="] = "!=", ["!="] = "==", [">"] = "<=", ["<"] = ">=", ["<="] = ">", [">="] = "<",
    };

    private static string Wrap(Expr e, int prec) => e.Prec < prec ? $"({e.Text})" : e.Text;

    private static Expr Atom(string text, int start, bool call = false, int? literal = null)
        => new() { Text = text, Prec = PAtom, Start = start, Call = call, Literal = literal };

    private static Expr MakeBinary(Expr a, Expr b, string op, int prec) => new()
    {
        Text = $"{Wrap(a, prec)} {op} {Wrap(b, prec + 1)}", Prec = prec, Start = Math.Min(a.Start, b.Start),
        Call = a.Call || b.Call, Op = op, Left = a, Right = b,
    };

    private static Expr Not(Expr e)
    {
        if (e.Op != null && Inverse.TryGetValue(e.Op, out var inv))
            return MakeBinary(e.Left, e.Right, inv, e.Prec);
        if (e.Op == "!" && e.Left != null) return e.Left;
        return new Expr { Text = "!" + Wrap(e, PUnary), Prec = PUnary, Start = e.Start, Call = e.Call, Op = "!", Left = e };
    }

    // ── Statements ──

    private enum Kind { Plain, CondJump, Jump, Open, Close }

    /// <summary>A flat statement. CondJump: jumps to Target when Cond is false (falls into the "then" part).</summary>
    private sealed class Stmt
    {
        public Kind Kind;
        public int Offset;
        public string Text;
        public Expr Cond;
        public int Target = -1;
    }

    /// <summary>Names an object id (e.g. from DOBJ); null when unknown.</summary>
    public delegate string ObjectNamer(int id);

    public static List<ScriptLine> Decompile(ReadOnlySpan<byte> code, int start, int end, ObjectNamer objectName = null)
    {
        var instructions = Disassemble(code, start, end);
        var stmts = new Builder(objectName).Run(instructions);
        int endOffset = instructions.Count > 0 ? instructions[^1].Offset + instructions[^1].Length : start;
        return new Structurer(stmts, endOffset).Run();
    }

    /// <summary>Plain text with a 4-digit offset gutter.</summary>
    public static string Format(IEnumerable<ScriptLine> lines, bool offsets = true)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            if (offsets) sb.Append(l.Offset >= 0 ? l.Offset.ToString("X4") : "    ").Append("  ");
            sb.Append(' ', l.Indent * 4).AppendLine(l.Text);
        }
        return sb.ToString();
    }

    // ── Pass 1: symbolic stack → statements ──

    private sealed class Builder(ObjectNamer objectName)
    {
        private readonly List<Stmt> _out = new();
        private List<Expr> _stack = new();
        private readonly Dictionary<int, List<Expr>> _stackAt = new();
        private int _temps;
        private string _actor = "?", _verb = "?";
        private StringBuilder _print;
        private int _printOffset;
        private byte _printOp;

        public List<Stmt> Run(List<ScriptInstruction> list)
        {
            bool dead = false;
            for (int i = 0; i < list.Count; i++)
            {
                var ins = list[i];
                if (dead)
                {
                    // Code after an unconditional jump is reached only by a jump: take the stack it saw.
                    // Restamped so statements built from it start at this label, where the jump lands.
                    _stack = _stackAt.TryGetValue(ins.Offset, out var s) ? s.Select(e => Restamp(e, ins.Offset)).ToList() : new List<Expr>();
                    dead = false;
                }
                if (_print != null && (ins.Opcode != _printOp || ins.SubOp == 0xC8)) FlushPrint(null);
                if (ins.Unknown)
                {
                    Emit(ins.Offset, $"// {ins.Name} — can't decode further");
                    break;
                }

                // An override is followed by the jump taken when the cutscene is skipped.
                if (ins.Opcode == 0x84 && i + 1 < list.Count && list[i + 1].Opcode == 0x66)
                {
                    _out.Add(new Stmt { Kind = Kind.Plain, Offset = ins.Offset, Text = "override", Target = list[i + 1].Target });
                    i++;
                    continue;
                }

                Step(ins);
                if (ins.Opcode == 0x66 || (ins.Opcode == 0x7b && ins.SubOp < 0))
                {
                    // Values left over at a jump belong to its target. At the end of a script, one value
                    // is what the script returns to call-script; more would be a decoding gap.
                    if (ins.Opcode == 0x7b && _stack.Count > 0)
                    {
                        _out.RemoveAt(_out.Count - 1); // the end-script just written
                        if (_stack.Count == 1) Emit(Math.Min(_stack[0].Start, ins.Offset), $"return {_stack[0].Text}");
                        else Emit(ins.Offset, $"// {_stack.Count} unused values on the stack: {string.Join(", ", _stack.Select(e => e.Text))}");
                        _out.Add(new Stmt { Kind = Kind.Plain, Offset = ins.Offset, Text = "end-script" });
                        _stack.Clear();
                    }
                    dead = true;
                }
            }
            if (_print != null) FlushPrint(null);
            return _out;
        }

        private static Expr Restamp(Expr e, int start) => new()
        {
            Text = e.Text, Prec = e.Prec, Start = start, Call = e.Call, Literal = e.Literal, Op = e.Op, Left = e.Left, Right = e.Right,
        };

        private Expr Pop(int at)
        {
            if (_stack.Count == 0) return Atom("?", at);
            var e = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return e;
        }

        private void Push(Expr e) => _stack.Add(e);

        /// <summary>A stack list: the count on top, then that many values (last pushed last).</summary>
        private Expr PopList(int at)
        {
            var count = Pop(at);
            if (count.Literal is not int n || n < 0 || n > 64)
                return Atom($"[list of {count.Text}]", count.Start);
            var items = new Expr[n];
            for (int k = n - 1; k >= 0; k--) items[k] = Pop(at);
            int start = n > 0 ? Math.Min(count.Start, items.Min(x => x.Start)) : count.Start;
            return Atom("[" + string.Join(", ", items.Select(x => x.Text)) + "]", start, items.Any(x => x.Call));
        }

        /// <summary>Pops arguments for an argument pattern; returns them in push order.</summary>
        private Expr[] PopArgs(string pattern, int at)
        {
            var args = new Expr[pattern.Length];
            for (int k = pattern.Length - 1; k >= 0; k--)
            {
                args[k] = pattern[k] == 'L' ? PopList(at) : Pop(at);
                if (pattern[k] == 'o' && args[k].Literal is int id && objectName?.Invoke(id) is { Length: > 0 } name)
                    args[k] = Atom(name, args[k].Start, literal: id);
            }
            return args;
        }

        private static int StartOf(Expr[] args, int at)
        {
            int s = at;
            foreach (var a in args) s = Math.Min(s, a.Start);
            return s;
        }

        private void Emit(int offset, string text, Kind kind = Kind.Plain)
            => _out.Add(new Stmt { Kind = kind, Offset = offset, Text = text });

        private void SaveStack(int target)
        {
            if (target >= 0 && !_stackAt.ContainsKey(target)) _stackAt[target] = new List<Expr>(_stack);
        }

        private static string Fill(string template, Expr[] args, ScriptInstruction ins)
        {
            if (template == null) return ins.Name;
            var sb = new StringBuilder(template.Length + 16);
            for (int i = 0; i < template.Length; i++)
            {
                char c = template[i];
                if (c == '{' && i + 2 < template.Length && template[i + 2] == '}')
                {
                    char k = template[i + 1];
                    if (k is >= '0' and <= '9' && k - '0' < args.Length) { sb.Append(args[k - '0'].Text); i += 2; continue; }
                    if (k == 's') { sb.Append(ins.Text ?? "\"\""); i += 2; continue; }
                    if (k == 'v') { sb.Append(VarName(ins.Operand)); i += 2; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private void Step(ScriptInstruction ins)
        {
            int at = ins.Offset;
            var def = ins.Def;
            switch (ins.Opcode)
            {
                case 0x01: Push(Atom(ins.Operand.ToString(), at, literal: ins.Operand)); return;
                case 0x02: Push(Atom(VarName(ins.Operand), at)); return;
                case 0x03:
                {
                    var i = Pop(at);
                    Push(Atom($"{VarName(ins.Operand)}[{i.Text}]", Math.Min(i.Start, at), i.Call));
                    return;
                }
                case 0x04:
                {
                    var b = Pop(at); var a = Pop(at);
                    Push(Atom($"{VarName(ins.Operand)}[{a.Text}][{b.Text}]", Math.Min(Math.Min(a.Start, b.Start), at), a.Call || b.Call));
                    return;
                }
                case 0x05: // dup: name a call so it isn't shown as called twice
                {
                    var e = Pop(at);
                    if (e.Call)
                    {
                        string t = $"$t{++_temps}";
                        Emit(e.Start, $"{t} = {e.Text}");
                        e = Atom(t, at);
                    }
                    Push(e); Push(Restamp(e, at));
                    return;
                }
                case 0x06:
                {
                    var e = Pop(at);
                    if (e.Call) Emit(e.Start, e.Text);
                    return;
                }
                case 0x07: Push(Not(Pop(at))); return;
                case 0x64 or 0x65:
                {
                    var c = Pop(at);
                    SaveStack(ins.Target);
                    _out.Add(new Stmt { Kind = Kind.CondJump, Offset = Math.Min(c.Start, at), Cond = ins.Opcode == 0x65 ? c : Not(c), Target = ins.Target });
                    return;
                }
                case 0x66:
                    SaveStack(ins.Target);
                    _out.Add(new Stmt { Kind = Kind.Jump, Offset = at, Target = ins.Target });
                    return;
            }

            if (Binary.TryGetValue(ins.Opcode, out var bin))
            {
                var b = Pop(at); var a = Pop(at);
                Push(MakeBinary(a, b, bin.Op, bin.Prec));
                return;
            }

            // Grouped ops whose arguments are popped before the sub-op (saveRestoreVerbs).
            string pattern = def.Args;
            if (ins.SubOp >= 0 && Ops[ins.Opcode].Args.Length > 0) pattern = Ops[ins.Opcode].Args;
            var args = PopArgs(pattern, at);
            int start = StartOf(args, at);

            switch (ins.Opcode)
            {
                case 0x79 or 0x7a or 0xc8 or 0x7d:
                {
                    // startScript(flags, script, args) / quick(script, args) / jumpToScript(flags, script, args)
                    bool flags = ins.Opcode is 0x79 or 0x7d;
                    var script = args[flags ? 1 : 0];
                    var list = args[flags ? 2 : 1];
                    if (ins.Opcode == 0xc8)
                    {
                        Push(Atom($"call-script {script.Text}{CallArgs(list, always: true)}", start, call: true));
                        return;
                    }
                    string verb = ins.Opcode switch { 0x7d => "chain-script", 0x79 => "start-script", _ => "start-script-quick" };
                    Emit(start, $"{verb}{(flags ? Flags(args[0]) : "")} {script.Text}{CallArgs(list)}");
                    return;
                }
                case 0x7f or 0xc9:
                {
                    // startObject(flags, obj, verb, args) / startObjectQuick(obj, verb, args)
                    bool flags = ins.Opcode == 0x7f;
                    int o = flags ? 1 : 0;
                    Emit(start, $"start-object{(flags ? Flags(args[0]) : "")} {args[o].Text} verb {args[o + 1].Text}{CallArgs(args[o + 2])}");
                    return;
                }
                case 0x81: Emit(start, $"cut-scene{CallArgs(args[0])} {{", Kind.Open); return;
                case 0x82: Emit(at, "}", Kind.Close); return;
                case 0xba: Emit(start, KernelSet(args[0])); return;
                case 0xd8: Push(Atom($"kernel-get{CallArgs(args[0], always: true)}", start, call: true)); return;
                case 0xac when ins.SubOp == 0x7A: _actor = args[0].Text; return;
                case 0xae when ins.SubOp == 0x96: _verb = args[0].Text; return;
                case 0x69 when ins.Target >= 0: SaveStack(ins.Target); break;
            }

            if (Ops[ins.Opcode].Subs != null && IsPrint(ins.Opcode))
            {
                Print(ins, args, start);
                return;
            }

            string text = Fill(def.Scumm, args, ins);
            if (ins.Opcode == 0xac) text = $"actor {_actor} {text}";
            else if (ins.Opcode == 0xae) text = $"verb {_verb} {text}";
            else if (ins.Opcode == 0xab) text = $"room {text}";

            if (def.Push) Push(Atom(text, start, call: true));
            else Emit(start, text);
        }

        private static bool IsPrint(byte op) => op is 0x8f or 0x90 or 0x93 or 0x94 or 0x95 or 0x96 or 0x97;

        /// <summary>Print ops come one sub-op per instruction; they're joined into one line.</summary>
        private void Print(ScriptInstruction ins, Expr[] args, int start)
        {
            if (ins.SubOp == 0xC8 || _print == null)
            {
                FlushPrint(null);
                _print = new StringBuilder(ins.Opcode switch
                {
                    0x8f => $"print-actor {(args.Length > 0 ? args[0].Text : "?")}",
                    0x90 => "print-ego",
                    0x94 => "print-text",
                    0x95 => "print-debug",
                    0x96 => "print-system",
                    0x97 => "blast-text",
                    _ => "print-line",
                });
                _printOp = ins.Opcode;
                _printOffset = start;
                if (ins.SubOp == 0xC8) return;
            }
            if (ins.SubOp == 0xC9) { FlushPrint(" set-default"); return; }
            _print.Append(' ').Append(Fill(ins.Def.Scumm, args, ins));
            if (ins.SubOp == 0xD1) FlushPrint(null);
        }

        private void FlushPrint(string suffix)
        {
            if (_print == null) return;
            if (suffix != null) _print.Append(suffix);
            Emit(_printOffset, _print.ToString());
            _print = null;
        }

        private static string Flags(Expr flags) => flags.Literal switch
        {
            0 => "",
            int f => ((f & 1) != 0 ? " bak" : "") + ((f & 2) != 0 ? " rec" : "") + ((f & ~3) != 0 ? $" flags {f}" : ""),
            _ => $" flags {flags.Text}",
        };

        /// <summary>"[a, b]" → "(a, b)"; an empty list → "" (or "()" when <paramref name="always"/>).</summary>
        private static string CallArgs(Expr list, bool always = false)
        {
            string t = list.Text;
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                string inner = t[1..^1];
                return inner.Length == 0 && !always ? "" : $"({inner})";
            }
            return $" {t}";
        }

        // kernelSetFunctions: the first list item picks the function (ScummVM o8_kernelSetFunctions).
        private static readonly Dictionary<int, string> KernelSetNames = new()
        {
            [11] = "lock-object", [12] = "unlock-object", [13] = "remap-costume", [14] = "remap-costume-insert",
            [15] = "set-video-frame-rate", [20] = "set-box-scale-slot", [21] = "set-scale-slot",
            [22] = "set-banner-colors", [23] = "set-actor-chore-limb-frame", [24] = "clear-text-queue",
            [25] = "save-game-read-name", [26] = "save-game-write", [27] = "save-game-read",
            [28] = "save-game-stamp-screenshot", [29] = "set-key-script", [30] = "kill-all-scripts-but-me",
            [31] = "stop-all-video", [32] = "write-registry-value", [33] = "palette-set-intensity",
            [34] = "query-quit", [108] = "build-palette-shadow", [109] = "set-palette-shadow",
            [118] = "blast-shadow-object", [119] = "super-blast-object",
        };

        private static string KernelSet(Expr list)
        {
            string t = list.Text;
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                var parts = t[1..^1].Split(", ", 2);
                if (parts.Length > 0 && int.TryParse(parts[0], out int fn) && KernelSetNames.TryGetValue(fn, out var name))
                    return parts.Length > 1 ? $"{name} {parts[1]}" : name;
            }
            return $"kernel-set{CallArgs(list, always: true)}";
        }
    }

    // ── Pass 2: jumps → blocks ──

    private sealed class Structurer(List<Stmt> stmts, int endOffset)
    {
        private readonly List<ScriptLine> _lines = new();
        private readonly HashSet<int> _labels = new();
        private int _extraIndent;
        /// <summary>Enclosing loops: a jump to Head is a continue (unless the test is at the end), to Exit a break.</summary>
        private readonly List<(int Head, int Exit, bool TestAtEnd)> _loops = new();

        private int OffsetOf(int index) => index < stmts.Count ? stmts[index].Offset : endOffset;

        /// <summary>"break" / "continue" for a jump to the innermost loop's exit / head, else null.</summary>
        private string LoopJump(int target)
        {
            if (_loops.Count == 0) return null;
            var loop = _loops[^1];
            if (target == loop.Exit) return "break";
            if (target == loop.Head && !loop.TestAtEnd) return "continue";
            return null;
        }

        private void Body(int i, int j, int indent, int head, int exit, bool testAtEnd, int noLoopAt = -1)
        {
            _loops.Add((head, exit, testAtEnd));
            Emit(i, j, indent, noLoopAt);
            _loops.RemoveAt(_loops.Count - 1);
        }

        public List<ScriptLine> Run()
        {
            Emit(0, stmts.Count, 0);
            if (_labels.Count == 0) return _lines;

            // Put each goto's label before the first line at (or after) its target.
            var result = new List<ScriptLine>(_lines.Count + _labels.Count);
            var pending = new SortedSet<int>(_labels);
            foreach (var line in _lines)
            {
                while (pending.Count > 0 && line.Offset >= 0 && pending.Min <= line.Offset)
                {
                    int t = pending.Min;
                    pending.Remove(t);
                    result.Add(new ScriptLine(t, 0, $"L{t:X4}:"));
                }
                result.Add(line);
            }
            foreach (int t in pending) result.Add(new ScriptLine(t, 0, $"L{t:X4}:"));
            return result;
        }

        private string Goto(int target)
        {
            if (LoopJump(target) is { } word) return word;
            _labels.Add(target);
            return $"goto L{target:X4}";
        }

        /// <summary>Index of the statement starting at <paramref name="offset"/>; Count for the end; -1 if inside one.</summary>
        private int IndexAt(int offset)
        {
            if (offset >= endOffset) return offset == endOffset ? stmts.Count : -1;
            int lo = 0, hi = stmts.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (stmts[mid].Offset == offset) return mid;
                if (stmts[mid].Offset < offset) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        private void Line(int offset, int indent, string text)
            => _lines.Add(new ScriptLine(offset, Math.Max(0, indent + _extraIndent), text));

        private void Emit(int i, int j, int indent, int noLoopAt = -1)
        {
            while (i < j)
            {
                var s = stmts[i];
                if (i != noLoopAt && TryLoop(i, j, indent) is int after)
                {
                    i = after;
                    continue;
                }
                if (s.Kind == Kind.CondJump && TryBlock(i, j, indent, elseIf: false) is int next)
                {
                    i = next;
                    continue;
                }

                switch (s.Kind)
                {
                    case Kind.CondJump:
                        Line(s.Offset, indent, $"if ({Not(s.Cond).Text}) {Goto(s.Target)}");
                        break;
                    case Kind.Jump:
                        Line(s.Offset, indent, Goto(s.Target));
                        break;
                    case Kind.Open:
                        Line(s.Offset, indent, s.Text);
                        _extraIndent++;
                        break;
                    case Kind.Close:
                        _extraIndent = Math.Max(0, _extraIndent - 1);
                        Line(s.Offset, indent, s.Text);
                        break;
                    default:
                        if (s.Target >= 0) // override: where a skipped cutscene continues
                        {
                            _labels.Add(s.Target);
                            Line(s.Offset, indent, $"{s.Text}  // skip to L{s.Target:X4}");
                        }
                        else Line(s.Offset, indent, s.Text);
                        break;
                }
                i++;
            }
        }

        /// <summary>
        /// do { … } while (c): a later conditional jump at this level jumps back here. A plain jump
        /// back makes do { … } while (true), unless this is a while head (handled by TryBlock).
        /// </summary>
        private int? TryLoop(int i, int j, int indent)
        {
            int start = stmts[i].Offset, back = -1;
            for (int k = j - 1; k > i; k--)
                if (stmts[k].Target == start && stmts[k].Kind is Kind.CondJump or Kind.Jump) { back = k; break; }
            if (back < 0) return null;
            var tail = stmts[back];
            if (tail.Kind == Kind.Jump && stmts[i].Kind == Kind.CondJump && Extent(i, j) == back + 1) return null; // while
            Line(start, indent, "do {");
            Body(i, back, indent + 1, start, OffsetOf(back + 1), tail.Kind == Kind.CondJump, noLoopAt: i);
            Line(tail.Offset, indent, tail.Kind == Kind.CondJump ? $"}} while ({Not(tail.Cond).Text})" : "} while (true)");
            return back + 1;
        }

        /// <summary>
        /// Writes an if / if-else / while starting at statement <paramref name="i"/> when its jumps
        /// stay inside [i, j); returns the index after it, or null.
        /// </summary>
        private int? TryBlock(int i, int j, int indent, bool elseIf)
        {
            var s = stmts[i];
            if (s.Target <= s.Offset) return null;
            int t = IndexAt(s.Target);
            if (t < 0 || t > j || t <= i) return null;

            int last = t - 1;
            if (last > i && stmts[last].Kind == Kind.Jump)
            {
                var jump = stmts[last];
                // while: the body ends by jumping back to the condition.
                if (jump.Target == s.Offset && !elseIf)
                {
                    Line(s.Offset, indent, $"while ({s.Cond.Text}) {{");
                    Body(i + 1, last, indent + 1, s.Offset, OffsetOf(t), testAtEnd: false);
                    Line(-1, indent, "}");
                    return t;
                }
                // if / else: the "then" part jumps over the "else" part.
                if (jump.Target > jump.Offset)
                {
                    int e = IndexAt(jump.Target);
                    if (e > t && e <= j && last == i + 1 && !elseIf)
                    {
                        // "if (c) {} else {…}": only a jump in the "then" part, so write the else part under !c.
                        Line(s.Offset, indent, $"if ({Not(s.Cond).Text}) {{");
                        Emit(t, e, indent + 1);
                        Line(-1, indent, "}");
                        return e;
                    }
                    if (e > t && e <= j)
                    {
                        Line(s.Offset, indent, elseIf ? $"}} else if ({s.Cond.Text}) {{" : $"if ({s.Cond.Text}) {{");
                        Emit(i + 1, last, indent + 1);
                        // "else if": the else part is a single if that fills it.
                        if (stmts[t].Kind == Kind.CondJump && Extent(t, e) == e)
                        {
                            TryBlock(t, e, indent, elseIf: true);
                            if (!elseIf) Line(-1, indent, "}");
                            return e;
                        }
                        Line(stmts[t].Offset, indent, "} else {");
                        Emit(t, e, indent + 1);
                        if (!elseIf) Line(-1, indent, "}");
                        return e;
                    }
                }
            }

            // "if (c) goto L" when the body is a lone jump out.
            if (!elseIf && t == i + 2 && stmts[i + 1].Kind == Kind.Jump && IndexAt(stmts[i + 1].Target) is var g && (g < i || g > j))
            {
                Line(s.Offset, indent, $"if ({s.Cond.Text}) {Goto(stmts[i + 1].Target)}");
                return t;
            }
            Line(s.Offset, indent, elseIf ? $"}} else if ({s.Cond.Text}) {{" : $"if ({s.Cond.Text}) {{");
            Emit(i + 1, t, indent + 1);
            if (!elseIf) Line(-1, indent, "}");
            return t;
        }

        /// <summary>Where an if starting at <paramref name="i"/> would end (without writing it), or -1.</summary>
        private int Extent(int i, int j)
        {
            var s = stmts[i];
            if (s.Kind != Kind.CondJump || s.Target <= s.Offset) return -1;
            int t = IndexAt(s.Target);
            if (t < 0 || t > j || t <= i) return -1;
            int last = t - 1;
            if (last > i && stmts[last].Kind == Kind.Jump && stmts[last].Target > stmts[last].Offset)
            {
                int e = IndexAt(stmts[last].Target);
                if (e > t && e <= j) return e;
            }
            return t;
        }
    }
}
