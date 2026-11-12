using System;
using System.Collections.Generic;
using System.Text;
using MMUCS.Core.Scumm;

namespace MMUCS.Desktop.Services;

public enum ScriptTokenKind { Plain, Offset, Bytes, Comment, Keyword, Command, Function, Variable, Number, String, Label }

/// <summary>A run of script text in one colour. Offsets are separate runs so the view can hide them.</summary>
public readonly record struct ScriptToken(ScriptTokenKind Kind, string Text);

/// <summary>
/// Splits the Script viewer's text (decompiled SCUMM or the instruction listing) into coloured runs.
/// Adjacent runs of one kind are merged, and whitespace joins the run before it, to keep the run count low.
/// </summary>
public static class ScriptHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "while", "do", "for", "goto", "break", "continue", "return", "true", "false", "in",
        "end-script", "end-object", "override", "object",
    };

    /// <summary>Decompiled SCUMM lines with a 6-character offset gutter.</summary>
    public static List<ScriptToken> Scumm(IEnumerable<ScriptLine> lines)
    {
        var o = new Sink();
        foreach (var l in lines)
        {
            o.Add(ScriptTokenKind.Offset, (l.Offset >= 0 ? l.Offset.ToString("X4") : "    ") + "  ");
            if (l.Indent > 0) o.Add(ScriptTokenKind.Plain, new string(' ', l.Indent * 4));
            Code(l.Text, o);
            o.Add(ScriptTokenKind.Plain, "\n");
        }
        return o.Finish();
    }

    /// <summary>The instruction listing: "OFFS  BYTES…  name operand → target", and "; label" lines.</summary>
    public static List<ScriptToken> Listing(string text)
    {
        var o = new Sink();
        foreach (var line in text.Split('\n'))
        {
            var s = line.TrimEnd('\r');
            if (s.StartsWith(';')) o.Add(ScriptTokenKind.Comment, s);
            else if (s.Length >= 20 && IsHex(s, 0, 4) && s[4] == ' ' && s[5] == ' ')
            {
                o.Add(ScriptTokenKind.Offset, s[..6]);
                o.Add(ScriptTokenKind.Bytes, s[6..20]);
                Code(s[20..], o);
            }
            else Code(s, o);
            o.Add(ScriptTokenKind.Plain, "\n");
        }
        return o.Finish();
    }

    /// <summary>One line of code (no gutter). Its first word is the command, unless it's assigned to.</summary>
    private static void Code(string s, Sink o)
    {
        bool head = true;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { o.Space(c); i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { o.Add(ScriptTokenKind.Comment, s[i..]); return; }
            if (c == '"')
            {
                int j = i + 1;
                while (j < s.Length && s[j] != '"') j += s[j] == '\\' ? 2 : 1;
                j = Math.Min(j + 1, s.Length);
                o.Add(ScriptTokenKind.String, s[i..j]);
                i = j; head = false; continue;
            }
            if (c == '→') // listing jump target, in hex
            {
                o.Add(ScriptTokenKind.Plain, "→");
                int j = ++i;
                while (j < s.Length && s[j] == ' ') o.Space(s[j++]);
                int k = j;
                while (k < s.Length && Uri.IsHexDigit(s[k])) k++;
                if (k > j) o.Add(ScriptTokenKind.Label, s[j..k]);
                i = k; continue;
            }
            if (char.IsDigit(c))
            {
                int j = i;
                while (j < s.Length && char.IsLetterOrDigit(s[j])) j++;
                o.Add(ScriptTokenKind.Number, s[i..j]);
                i = j; head = false; continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                int j = i + 1;
                while (j < s.Length)
                {
                    if (char.IsLetterOrDigit(s[j]) || s[j] == '_') j++;
                    else if (s[j] is '-' or '.' && j + 1 < s.Length && char.IsLetter(s[j + 1])) j += 2; // kebab-case names, op.sub-op
                    else break;
                }
                string w = s[i..j];
                int n = j;
                while (n < s.Length && s[n] == ' ') n++;
                char next = n < s.Length ? s[n] : '\0';
                var kind = Keywords.Contains(w) ? ScriptTokenKind.Keyword
                    : IsVariable(w) ? ScriptTokenKind.Variable
                    : IsLabel(w) ? ScriptTokenKind.Label
                    : next == '(' ? ScriptTokenKind.Function
                    : head && next != '=' && next != '[' ? ScriptTokenKind.Command
                    : ScriptTokenKind.Plain;
                o.Add(kind, w);
                i = j; head = false; continue;
            }
            o.Add(ScriptTokenKind.Plain, c.ToString());
            if (c != '}' && c != '{') head = false; // "} else …" still starts a statement
            i++;
        }
    }

    private static bool IsVariable(string w) =>
        Prefixed(w, "var") || Prefixed(w, "local") || Prefixed(w, "bit") || Prefixed(w, "$t");

    private static bool Prefixed(string w, string prefix)
    {
        if (w.Length <= prefix.Length || !w.StartsWith(prefix, StringComparison.Ordinal)) return false;
        for (int k = prefix.Length; k < w.Length; k++) if (!char.IsDigit(w[k])) return false;
        return true;
    }

    private static bool IsLabel(string w) => w.Length == 5 && w[0] == 'L' && IsHex(w, 1, 4);

    private static bool IsHex(string s, int start, int count)
    {
        for (int k = start; k < start + count; k++) if (!Uri.IsHexDigit(s[k])) return false;
        return true;
    }

    /// <summary>Collects runs, merging same-kind neighbours; whitespace joins the previous run (not an offset).</summary>
    private sealed class Sink
    {
        private readonly List<ScriptToken> _tokens = new();
        private readonly StringBuilder _sb = new();
        private ScriptTokenKind _kind = ScriptTokenKind.Plain;

        public void Add(ScriptTokenKind kind, string text)
        {
            if (text.Length == 0) return;
            if (kind != _kind && _sb.Length > 0) Flush();
            _kind = kind;
            _sb.Append(text);
        }

        public void Space(char c)
        {
            if (_kind == ScriptTokenKind.Offset) Add(ScriptTokenKind.Plain, c.ToString());
            else _sb.Append(c);
        }

        private void Flush()
        {
            _tokens.Add(new ScriptToken(_kind, _sb.ToString()));
            _sb.Clear();
        }

        public List<ScriptToken> Finish()
        {
            if (_sb.Length > 0) Flush();
            return _tokens;
        }
    }
}
