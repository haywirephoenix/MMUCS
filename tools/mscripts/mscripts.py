#!/usr/bin/env python3
"""mscripts: search an MMUCS script export and gather evidence for naming variables.

Reads the scripts/ tree that MMUCS's exporter writes (File > Export, Scripts). Needs Python 3.9+
and nothing else. Run `mscripts --help`, or `mscripts COMMAND --help` for a command's options.
"""
import argparse
import json
import os
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Tuple

TOOL_DIR = Path(__file__).resolve().parent
REPO_MAP = TOOL_DIR.parent.parent / "src" / "MMUCS.Core" / "Names" / "variables.json"
SAVED_ROOT = TOOL_DIR / ".export"

# Same rules as MMUCS (VariableNames.cs), plus the decompiler's keywords.
VALID_NAME = re.compile(r"^[a-z][a-z0-9]*(-[a-z][a-z0-9]*)*$")
RESERVED = re.compile(r"^(var|local|bit)\d+$")
KEYWORDS = {"if", "else", "while", "do", "for", "goto", "break", "continue", "return", "true", "false",
            "in", "end-script", "end-object", "override", "object"}

# A word as the decompiler writes it: kebab-case names, op.sub-op.
WORD = re.compile(r"[A-Za-z_$][A-Za-z0-9_$]*(?:[-.][A-Za-z][A-Za-z0-9_]*)*")
GUTTER = re.compile(r"^(?:([0-9A-F]{4})| {4})  ")
NUMBERED = re.compile(r"^(var|bit|local)(\d+)$")

USAGE = """usage: mscripts COMMAND [options]

Find your way around:
  use [DIR]                     Remember the export to read (the folder holding scripts/)
  rooms                         List rooms with their script counts
  script WHERE...               Print a script: "global 6", "9 local_2006", "9 entry", a path
  search REGEX [--in ROOM]...   Find lines, with -C for context and --count per script
  calls N [--in ROOM]           List who starts, calls or chains to script N

Name variables:
  vars [--unnamed] [--top N]    Rank variables by how many scripts use them, with their status
  var V                         Evidence for one variable: writes, comparisons, commands, rooms
  name V NAME --note TEXT       Name a variable in variables.json (checks the name first)
  note V TEXT                   Track a variable in variables.json without naming it

V is a number (266), varN, bitN or a name (cursor-object). The export is found from --root,
the MSCRIPTS_ROOT variable, the folder saved with `use`, or the current folder and its parents.
"""


# ── The export ──────────────────────────────────────────────────────────────────────────────────

class Line:
    __slots__ = ("no", "offset", "text")

    def __init__(self, no: int, offset: Optional[str], text: str):
        self.no, self.offset, self.text = no, offset, text


class Script:
    """One exported .scumm file: its place in the tree and its code lines (gutter removed)."""

    def __init__(self, root: Path, path: Path):
        self.path = path
        self.rel = path.relative_to(root).as_posix()
        parts = self.rel[:-len(".scumm")].split("/")
        self.room = parts[1] if parts[0] == "rooms" and len(parts) > 2 else None
        self.room_no = int(self.room.split("_")[0]) if self.room and self.room[:3].isdigit() else None
        self.name = parts[-1]
        if parts[0] == "global":
            self.kind = "global"
        elif len(parts) > 3 and parts[2] == "objects":
            self.kind = "object"
        else:
            self.kind = "room"
        raw = path.read_text(encoding="utf-8").split("\n")
        if raw and raw[-1] == "":
            raw.pop()
        self.header = []
        body_start = 0
        while body_start < len(raw) and raw[body_start].startswith("//"):
            self.header.append(raw[body_start])
            body_start += 1
        if body_start < len(raw) and raw[body_start] == "":
            body_start += 1
        body = raw[body_start:]
        gutter = bool(body) and all(GUTTER.match(l) or l == "" for l in body)
        self.lines = []
        for i, text in enumerate(body, start=body_start + 1):
            offset = None
            if gutter and text:
                m = GUTTER.match(text)
                offset, text = m.group(1), text[m.end():]
            self.lines.append(Line(i, offset, text))

    def where(self, line: Line) -> str:
        return f"{self.rel}:{line.no}"

    @property
    def label(self) -> str:
        return self.rel[:-len(".scumm")]


class Names:
    """Variable names: the export's index (what the files say) plus variables.json (status, notes, newer names)."""

    def __init__(self, scripts_root: Path, map_path: Optional[Path]):
        self.globals: Dict[int, str] = {}
        self.bits: Dict[int, str] = {}
        self.engine: set = set()
        self.map_path = map_path
        self.map: dict = {"Globals": {}, "Bits": {}}
        index = scripts_root / "variables.index.json"
        self.has_index = index.exists()
        if self.has_index:
            doc = json.loads(index.read_text(encoding="utf-8"))
            for k, e in (doc.get("Globals") or {}).items():
                self.globals[int(k)] = e["Name"]
                if e.get("Source") == "engine":
                    self.engine.add(int(k))
            for k, e in (doc.get("Bits") or {}).items():
                self.bits[int(k)] = e["Name"]
        if map_path and map_path.exists():
            self.map = json.loads(map_path.read_text(encoding="utf-8"))
            self.map.setdefault("Globals", {})
            self.map.setdefault("Bits", {})
        # Names added to the map since the export: the files still say varN, but the name resolves too.
        self.mapped_globals = {int(k): (e or {}).get("Name") or "" for k, e in self.map["Globals"].items() if k.isdigit()}
        self.mapped_bits = {int(k): (e or {}).get("Name") or "" for k, e in self.map["Bits"].items() if k.isdigit()}
        self.by_name: Dict[str, Tuple[str, int]] = {}
        for n, name in list(self.globals.items()) + [(n, s) for n, s in self.mapped_globals.items() if s]:
            self.by_name.setdefault(name, ("var", n))
        for n, name in list(self.bits.items()) + [(n, s) for n, s in self.mapped_bits.items() if s]:
            self.by_name.setdefault(name, ("bit", n))

    def section(self, kind: str) -> str:
        return "Globals" if kind == "var" else "Bits"

    def name_of(self, kind: str, n: int) -> str:
        if kind == "var":
            return self.mapped_globals.get(n) or self.globals.get(n, "")
        return self.mapped_bits.get(n) or self.bits.get(n, "")

    def note_of(self, kind: str, n: int) -> str:
        entry = self.map[self.section(kind)].get(str(n)) or {}
        return entry.get("Note") or ""

    def status(self, kind: str, n: int) -> str:
        if kind == "var" and n in self.engine:
            return "engine"
        entry = self.map[self.section(kind)].get(str(n))
        if entry is None:
            return "named" if self.name_of(kind, n) else ""
        return "named" if entry.get("Name") else "tracked"

    def aliases(self, kind: str, n: int) -> List[str]:
        out = [f"{kind}{n}"]
        for name in (self.globals.get(n) if kind == "var" else self.bits.get(n), self.name_of(kind, n)):
            if name and name not in out:
                out.append(name)
        return out

    def resolve(self, text: str) -> Tuple[str, int]:
        text = text.strip()
        if text.isdigit():
            return "var", int(text)
        m = NUMBERED.match(text)
        if m and m.group(1) != "local":
            return m.group(1), int(m.group(2))
        if text in self.by_name:
            return self.by_name[text]
        raise SystemExit(f"mscripts: no variable called '{text}' (use a number, varN, bitN or a known name)")

    def number_of_word(self, word: str) -> Optional[Tuple[str, int]]:
        m = NUMBERED.match(word)
        if m:
            return None if m.group(1) == "local" else (m.group(1), int(m.group(2)))
        return self.by_name.get(word)


class Export:
    def __init__(self, scripts_root: Path, map_path: Optional[Path]):
        self.root = scripts_root
        self.names = Names(scripts_root, map_path)
        self._scripts: Optional[List[Script]] = None

    @property
    def scripts(self) -> List[Script]:
        if self._scripts is None:
            self._scripts = [Script(self.root, p) for p in sorted(self.root.rglob("*.scumm"))]
            if not self._scripts:
                raise SystemExit(f"mscripts: no .scumm files under {self.root}")
        return self._scripts

    def select(self, rooms: List[str], only_global: bool = False, only_rooms: bool = False) -> Iterable[Script]:
        for s in self.scripts:
            if only_global and s.kind != "global":
                continue
            if only_rooms and s.kind == "global":
                continue
            if rooms and not any(room_matches(s, r) for r in rooms):
                continue
            yield s


def room_matches(s: Script, wanted: str) -> bool:
    if s.room is None:
        return False
    if wanted.isdigit():
        return s.room_no == int(wanted)
    return wanted.lower() in s.room.lower()


def find_scripts_root(arg: Optional[str]) -> Path:
    def as_root(p: Path) -> Optional[Path]:
        for c in (p / "scripts", p):
            if (c / "global").is_dir() or (c / "rooms").is_dir():
                return c
        return None

    if arg:
        found = as_root(Path(arg))
        if not found:
            raise SystemExit(f"mscripts: no script export in {arg} (expected scripts/global or scripts/rooms)")
        return found
    env = os.environ.get("MSCRIPTS_ROOT")
    if env and as_root(Path(env)):
        return as_root(Path(env))
    if SAVED_ROOT.exists():
        saved = SAVED_ROOT.read_text(encoding="utf-8").strip()
        if saved and as_root(Path(saved)):
            return as_root(Path(saved))
    cwd = Path.cwd()
    for p in [cwd] + list(cwd.parents):
        found = as_root(p)
        if found:
            return found
    raise SystemExit("mscripts: can't find a script export. Export scripts from MMUCS, then run "
                     "`mscripts use FOLDER`, or pass --root FOLDER.")


# ── Reading a variable's uses ───────────────────────────────────────────────────────────────────

def alias_pattern(aliases: List[str]) -> str:
    names = "|".join(re.escape(a) for a in sorted(aliases, key=len, reverse=True))
    return rf"(?<![\w$.\-])(?:{names})(?![\w$\-]|\.[A-Za-z])"


def leading_command(text: str, names: Names) -> str:
    """The command words a line starts with ('cursor image', 'print-actor'), or ''."""
    t = re.sub(r"^\}\s*", "", text.strip())
    words = []
    for m in re.finditer(r"\S+", t):
        w = m.group(0)
        if not re.fullmatch(r"[a-z][a-z0-9]*(?:[-.][a-z][a-z0-9]*)*", w):
            break
        if w in KEYWORDS or names.number_of_word(w) or NUMBERED.match(w):
            break
        words.append(w)
    if words and t[len(" ".join(words)):].lstrip().startswith(("=", "[", "(")):
        return ""  # an assignment or a call, not a command
    return " ".join(words)


def enclosing_calls(text: str, var: re.Pattern) -> List[str]:
    """Names of the calls, such as class-of(...), whose arguments contain the variable."""
    found = []
    for m in re.finditer(r"([a-z][a-z0-9]*(?:-[a-z][a-z0-9]*)*)\(", text):
        depth, i = 1, m.end()
        while i < len(text) and depth:
            depth += {"(": 1, ")": -1}.get(text[i], 0)
            i += 1
        if var.search(text, m.end(), i):
            found.append(m.group(1))
    return found


class Uses:
    """Everything one variable is used for, collected over the export."""

    def __init__(self):
        self.refs = 0
        self.scripts = set()
        self.writes: Dict[str, List[Tuple[Script, Line]]] = defaultdict(list)
        self.constants: Dict[int, List[Tuple[Script, Line]]] = defaultdict(list)
        self.compares: Counter = Counter()
        self.tests: List[Tuple[Script, Line]] = []
        self.commands: Counter = Counter()
        self.calls: Counter = Counter()
        self.copied_into: Counter = Counter()
        self.used_in: Counter = Counter()
        self.rooms: Counter = Counter()
        self.lines: List[Tuple[Script, Line]] = []


def collect(export: Export, kind: str, n: int) -> Uses:
    names = export.names
    aliases = names.aliases(kind, n)
    a = alias_pattern(aliases)
    var = re.compile(a)
    write = re.compile(rf"^{a}((?:\[[^\]]*\])*)\s*=(?!=)\s*(.*)$")
    cmp_after = re.compile(rf"{a}\s*(==|!=|<=|>=|<|>)\s*(-?\w[\w-]*)")
    cmp_before = re.compile(rf"(-?\w[\w-]*)\s*(==|!=|<=|>=|<|>)\s*{a}")
    in_list = re.compile(rf"{a}\s+in\s+\[([^\]]*)\]")
    copy = re.compile(rf"^([\w$-]+(?:\[[^\]]*\])*)\s*=\s*{a}$")
    assign = re.compile(r"^([\w$-]+)(?:\[[^\]]*\])*\s*=(?!=)")
    flip = {"<": ">", ">": "<", "<=": ">=", ">=": "<="}
    u = Uses()
    for s in export.scripts:
        for line in s.lines:
            t = line.text.strip()
            hits = len(var.findall(t))
            if not hits:
                continue
            u.refs += hits
            u.scripts.add(s.rel)
            u.rooms[s.room or "global"] += 1
            u.lines.append((s, line))
            m = write.match(t)
            if m:
                rhs = m.group(2).strip()
                index = m.group(1)
                if re.fullmatch(rf"{a}\s*\+\s*1", rhs):
                    form = "+ 1 (increment)"
                elif re.fullmatch(rf"{a}\s*-\s*1", rhs):
                    form = "- 1 (decrement)"
                elif re.fullmatch(r"-?\d+", rhs) and not index:
                    u.constants[int(rhs)].append((s, line))
                    continue
                else:
                    form = ("[...] " if index else "") + "= " + rhs
                u.writes[form].append((s, line))
                continue
            for m in cmp_after.finditer(t):
                u.compares[f"{m.group(1)} {m.group(2)}"] += 1
            for m in cmp_before.finditer(t):
                if not var.fullmatch(m.group(1)):
                    u.compares[f"{flip.get(m.group(2), m.group(2))} {m.group(1)}"] += 1
            for m in in_list.finditer(t):
                u.compares[f"in [{m.group(1)}]"] += 1
            stripped = cmp_after.sub("", cmp_before.sub("", in_list.sub("", t)))
            if re.match(r"^(\}\s*)?(else\s+)?(if|while)\s*\(|^\}\s*while\s*\(", t) and var.search(stripped):
                u.tests.append((s, line))
            m = copy.match(t)
            if m:
                u.copied_into[m.group(1)] += 1
            else:
                m = assign.match(t)
                if m:
                    u.used_in[m.group(1)] += 1
            cmd = leading_command(t, names)
            if cmd and var.search(t):
                u.commands[cmd] += 1
            for c in enclosing_calls(t, var):
                u.calls[c] += 1
    return u


# ── Commands ────────────────────────────────────────────────────────────────────────────────────

def out(text: str = ""):
    print(text)


def cmd_use(args, export_root_arg):
    if not args.dir:
        if SAVED_ROOT.exists():
            out(SAVED_ROOT.read_text(encoding="utf-8").strip())
        else:
            out("No export saved. Run: mscripts use FOLDER")
        return 0
    root = find_scripts_root(args.dir)
    SAVED_ROOT.write_text(str(root.parent if root.name == "scripts" else root), encoding="utf-8")
    out(f"Using {root}")
    return 0


def cmd_rooms(args, export: Export):
    counts = Counter(s.room for s in export.scripts if s.room)
    out(f"{'room':<28} scripts")
    for room in sorted(counts):
        out(f"{room:<28} {counts[room]}")
    out(f"\n{len(counts)} rooms, {sum(1 for s in export.scripts if s.kind == 'global')} global scripts")
    return 0


def cmd_script(args, export: Export):
    where = [w.strip("/") for w in args.where]
    matches = []
    if len(where) == 2 and where[0] == "global" and where[1].isdigit():
        matches = [s for s in export.scripts if s.kind == "global" and s.name == f"script_{int(where[1]):04d}"]
    elif len(where) == 1 and where[0].isdigit():
        matches = [s for s in export.scripts if s.kind == "global" and s.name == f"script_{int(where[0]):04d}"]
    elif len(where) == 2:
        room, name = where
        matches = [s for s in export.scripts if room_matches(s, room) and (s.name == name or s.name.startswith(name))]
    else:
        text = " ".join(where).replace("\\", "/").removesuffix(".scumm")
        matches = [s for s in export.scripts if text in s.rel]
    if not matches:
        raise SystemExit("mscripts: no script matches. Try: script global 6 | script 9 local_2006 | script 9 entry")
    if len(matches) > 1:
        out("Several scripts match:")
        for s in matches[:40]:
            out(f"  {s.rel}")
        return 1
    s = matches[0]
    for h in s.header:
        out(h)
    out(f"// {s.rel}")
    out()
    for line in s.lines:
        out(line.text if args.plain else f"{line.no:5}  {line.text}")
    return 0


def cmd_search(args, export: Export):
    flags = re.IGNORECASE if args.ignore_case else 0
    try:
        pattern = re.compile(args.pattern, flags)
    except re.error as e:
        raise SystemExit(f"mscripts: bad regex: {e}")
    total, per_script = 0, Counter()
    for s in export.select(args.room, args.only_global, args.only_rooms):
        hits = [i for i, l in enumerate(s.lines) if pattern.search(l.text)]
        if not hits:
            continue
        total += len(hits)
        per_script[s.rel] = len(hits)
        if args.count or args.files:
            continue
        shown = set()
        for i in hits:
            lo, hi = max(0, i - args.context), min(len(s.lines), i + args.context + 1)
            if args.context and shown and lo > max(shown) + 1:
                out("  --")
            for j in range(lo, hi):
                if j in shown:
                    continue
                shown.add(j)
                mark = ":" if j == i or j in hits else "-"
                out(f"{s.rel}{mark}{s.lines[j].no}{mark} {s.lines[j].text.strip()}")
            if args.limit and total >= args.limit:
                out(f"... stopped at {args.limit} matches (--limit)")
                return 0
    if args.files:
        for rel in per_script:
            out(rel)
    elif args.count:
        for rel, c in per_script.most_common():
            out(f"{c:6}  {rel}")
    out(f"\n{total} matches in {len(per_script)} scripts" if total else "No matches.")
    return 0


def cmd_calls(args, export: Export):
    n = args.script
    pattern = re.compile(rf"\b(start-script-quick|start-script|chain-script|call-script)((?: bak| rec)*|(?: flags \S+))\s+{n}(?![\w-])")
    found = 0
    by_room = defaultdict(list)
    for s in export.select(args.room):
        for line in s.lines:
            m = pattern.search(line.text)
            if m:
                by_room[s.room or "global"].append((s, line, m.group(1)))
                found += 1
    if not found:
        out(f"Nothing starts or calls script {n}.")
        return 0
    if n >= 2000:
        out(f"Local scripts are numbered per room: {n} in one room is a different script from {n} in another.\n")
    for room in sorted(by_room):
        for s, line, verb in by_room[room]:
            out(f"{s.where(line)}  {line.text.strip()}")
    out(f"\n{found} calls from {len({s.rel for r in by_room.values() for s, _, _ in r})} scripts")
    return 0


def cmd_vars(args, export: Export):
    names = export.names
    want = "bit" if args.bits else "var"
    refs, scripts, writes, rooms = Counter(), defaultdict(set), Counter(), defaultdict(set)
    for s in export.scripts:
        for line in s.lines:
            t = line.text.strip()
            seen_here = set()
            for m in WORD.finditer(t):
                key = names.number_of_word(m.group(0))
                if not key or key[0] != want:
                    continue
                n = key[1]
                refs[n] += 1
                scripts[n].add(s.rel)
                rooms[n].add(s.room or "global")
                if m.start() == 0 and n not in seen_here and re.match(r"(?:\[[^\]]*\])*\s*=(?!=)", t[m.end():]):
                    writes[n] += 1
                seen_here.add(n)
    rows = []
    for n in refs:
        status = names.status(want, n)
        if args.unnamed and names.name_of(want, n):
            continue
        if args.tracked and status != "tracked":
            continue
        rows.append((n, names.name_of(want, n), status, len(scripts[n]), refs[n], writes[n], len(rooms[n] - {"global"})))
    key = {"scripts": lambda r: (-r[3], -r[4]), "refs": lambda r: (-r[4], -r[3]), "num": lambda r: r[0]}[args.sort]
    rows.sort(key=key)
    out(f"{'var':>7}  {'name':<24} {'status':<8} {'scripts':>7} {'refs':>6} {'writes':>6} {'rooms':>5}  note")
    for n, name, status, sc, rf, wr, rm in rows[:args.top]:
        note = names.note_of(want, n)
        note = (note[:60] + "...") if len(note) > 63 else note
        out(f"{want + str(n):>7}  {name:<24} {status:<8} {sc:>7} {rf:>6} {wr:>6} {rm:>5}  {note}")
    shown = min(len(rows), args.top)
    out(f"\n{shown} of {len(rows)} shown. writes = lines that assign it; rooms = rooms (not global scripts) using it.")
    if not names.has_index:
        out("No variables.index.json in this export: re-export from a newer MMUCS so names resolve to numbers.")
    return 0


def cmd_var(args, export: Export):
    names = export.names
    kind, n = names.resolve(args.var)
    u = collect(export, kind, n)
    name = names.name_of(kind, n)
    status = names.status(kind, n)
    title = f"{kind}{n}" + (f"  {name}" if name else "") + (f"  ({status})" if status else "")
    out(title)
    note = names.note_of(kind, n)
    if note:
        out(f"  note: {note}")
    if not u.refs:
        out("Not used in any script.")
        return 0
    global_scripts = sum(1 for r in u.scripts if r.startswith("global/"))
    room_count = len([r for r in u.rooms if r != "global"])
    out(f"{u.refs} refs in {len(u.scripts)} scripts: {global_scripts} global, {len(u.scripts) - global_scripts} in {room_count} rooms")

    def first(places):
        s, line = places[0]
        more = f" (+{len(places) - 1})" if len(places) > 1 else ""
        return f"{s.where(line)}{more}"

    # A few constants are telling (a flag set to 0 and 1); many (object ids, line numbers) read as one row.
    writes = dict(u.writes)
    if 0 < len(u.constants) <= 6:
        for value, places in u.constants.items():
            writes[f"= {value}"] = places
    elif u.constants:
        values = sorted(u.constants)
        places = [p for v in values for p in u.constants[v]]
        writes[f"= a constant: {len(values)} values, {values[0]} to {values[-1]}"] = places
    nwrites = sum(len(v) for v in writes.values())
    out(f"\nWrites: {nwrites}")
    for form, places in sorted(writes.items(), key=lambda kv: -len(kv[1]))[:args.top]:
        out(f"  {len(places):5}x  {form:<40} {first(places)}")
    if len(writes) > args.top:
        out(f"  ... {len(writes) - args.top} more kinds of write (--top)")
    writers = {s.rel for places in writes.values() for s, _ in places}
    if writers and len(writers) <= 6:
        out(f"  written only in: {', '.join(sorted(writers))}")

    def counted(title, counter: Counter):
        if counter:
            out(f"\n{title}")
            for k, c in counter.most_common(args.top):
                out(f"  {c:5}x  {k}")

    counted("Compared with:", u.compares)
    if u.tests:
        out(f"\nTested as true/false: {len(u.tests)}x   e.g. {first(u.tests)}")
    counted("Passed to commands:", u.commands)
    counted("Inside calls:", u.calls)
    counted("Copied into:", u.copied_into)
    counted("Used to work out:", u.used_in)
    out("\nRooms:")
    out("  " + ", ".join(f"{r} {c}" for r, c in u.rooms.most_common(args.top)))
    if args.examples:
        out(f"\nExamples:")
        step = max(1, len(u.lines) // args.examples)
        for s, line in u.lines[::step][:args.examples]:
            out(f"  {s.where(line)}  {line.text.strip()}")
    return 0


def map_path_for(args) -> Path:
    if args.map:
        return Path(args.map)
    if args.local:
        base = os.environ.get("LOCALAPPDATA") or str(Path.home() / ".local" / "share")
        return Path(base) / "MMUCS" / "variables.json"
    return REPO_MAP


def load_map(path: Path) -> dict:
    if path.exists():
        doc = json.loads(path.read_text(encoding="utf-8"))
    else:
        doc = {}
    doc.setdefault("Globals", {})
    doc.setdefault("Bits", {})
    return doc


def save_map(path: Path, doc: dict):
    for section in ("Globals", "Bits"):
        doc[section] = {k: doc[section][k] for k in sorted(doc[section], key=lambda k: int(k) if k.isdigit() else 1 << 30)}
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:  # LF, like the rest of the repo
        f.write(json.dumps(doc, indent=2, ensure_ascii=False) + "\n")


def check_name(names: Names, doc: dict, kind: str, n: int, name: str) -> Optional[str]:
    if not VALID_NAME.match(name):
        return "names are lower-case kebab-case, each part starting with a letter (e.g. cursor-object, timer2)"
    if RESERVED.match(name):
        return f"'{name}' would read as a numbered variable"
    if name in KEYWORDS:
        return f"'{name}' is a decompiler keyword"
    if kind == "var" and n in names.engine:
        return f"var{n} is an engine variable ({names.globals.get(n)}); its name is fixed"
    for section, k in (("Globals", "var"), ("Bits", "bit")):
        for key, e in doc[section].items():
            if (e or {}).get("Name") == name and not (k == kind and key == str(n)):
                return f"'{name}' is already {k}{key}"
    for m, existing in names.globals.items():
        if existing == name and not (kind == "var" and m == n):
            return f"'{name}' is already var{m} in this export"
    return None


def cmd_name(args, export: Export):
    names = export.names
    kind, n = names.resolve(args.var)
    path = map_path_for(args)
    doc = load_map(path)
    section = names.section(kind)
    entry = dict(doc[section].get(str(n)) or {})
    name = args.name.strip()
    if name:
        problem = check_name(names, doc, kind, n, name)
        if problem:
            raise SystemExit(f"mscripts: not named: {problem}")
    note = args.note if args.note is not None else entry.get("Note")
    if name and not note:
        raise SystemExit("mscripts: give a --note saying how you know (where it's used, what it's compared with)")
    old = entry.get("Name") or ""
    entry["Name"] = name
    if note:
        entry["Note"] = note
    doc[section][str(n)] = entry
    save_map(path, doc)
    change = f"{old} -> {name}" if old and name else (f"named {name}" if name else f"name cleared (was {old})" if old else "tracked")
    out(f"{kind}{n}: {change}  ({path})")
    if name:
        out("Re-export scripts from MMUCS to see the name in the files; mscripts already resolves it.")
    return 0


def cmd_note(args, export: Export):
    names = export.names
    kind, n = names.resolve(args.var)
    path = map_path_for(args)
    doc = load_map(path)
    section = names.section(kind)
    entry = dict(doc[section].get(str(n)) or {"Name": ""})
    entry.setdefault("Name", "")
    entry["Note"] = args.text
    doc[section][str(n)] = entry
    save_map(path, doc)
    out(f"{kind}{n}: note saved  ({path})")
    return 0


# ── Command line ────────────────────────────────────────────────────────────────────────────────

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="mscripts", usage=USAGE, add_help=False)
    p.add_argument("--root", help="the export folder (holding scripts/)")
    p.add_argument("-h", "--help", action="store_true")
    sub = p.add_subparsers(dest="command")

    s = sub.add_parser("use", help="remember the export to read")
    s.add_argument("dir", nargs="?")

    sub.add_parser("rooms", help="list rooms")

    s = sub.add_parser("script", help="print a script")
    s.add_argument("where", nargs="+")
    s.add_argument("--plain", action="store_true", help="no line numbers")

    s = sub.add_parser("search", help="find lines matching a regex")
    s.add_argument("pattern")
    s.add_argument("--in", dest="room", action="append", default=[], help="room number or part of its name (repeatable)")
    s.add_argument("--global", dest="only_global", action="store_true", help="only global scripts")
    s.add_argument("--rooms", dest="only_rooms", action="store_true", help="only room scripts")
    s.add_argument("-C", "--context", type=int, default=0, help="lines of context")
    s.add_argument("-i", "--ignore-case", action="store_true")
    s.add_argument("--count", action="store_true", help="matches per script")
    s.add_argument("-l", "--files", action="store_true", help="only the scripts")
    s.add_argument("--limit", type=int, default=0, help="stop after N matches")

    s = sub.add_parser("calls", help="who starts or calls a script")
    s.add_argument("script", type=int)
    s.add_argument("--in", dest="room", action="append", default=[])

    s = sub.add_parser("vars", help="rank variables")
    s.add_argument("--unnamed", action="store_true", help="only variables without a name")
    s.add_argument("--tracked", action="store_true", help="only variables listed in the map without a name")
    s.add_argument("--bits", action="store_true", help="bit variables instead of globals")
    s.add_argument("--sort", choices=["scripts", "refs", "num"], default="scripts")
    s.add_argument("--top", type=int, default=40)
    s.add_argument("--map", help="variables.json to read status from")

    s = sub.add_parser("var", help="evidence for one variable")
    s.add_argument("var")
    s.add_argument("--top", type=int, default=12, help="rows per section")
    s.add_argument("--examples", type=int, default=8, help="example lines (0 for none)")
    s.add_argument("--map", help="variables.json to read status from")

    for name, helptext in (("name", "name a variable"), ("note", "track a variable with a note")):
        s = sub.add_parser(name, help=helptext)
        s.add_argument("var")
        if name == "name":
            s.add_argument("name", help='the name ("" clears it)')
            s.add_argument("--note", help="how you know")
        else:
            s.add_argument("text")
        where = s.add_mutually_exclusive_group()
        where.add_argument("--map", help="variables.json to change (default: the repo's)")
        where.add_argument("--local", action="store_true", help="change the app data copy MMUCS reads on top")
    return p


def main(argv: Optional[List[str]] = None) -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except AttributeError:
        pass
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.help or not args.command:
        out(USAGE.rstrip())
        return 0
    if args.command == "use":
        return cmd_use(args, args.root)
    scripts_root = find_scripts_root(args.root)
    map_arg = getattr(args, "map", None)
    if args.command in ("name", "note"):
        map_path = map_path_for(args)
    else:
        map_path = Path(map_arg) if map_arg else (REPO_MAP if REPO_MAP.exists() else None)
    export = Export(scripts_root, map_path)
    handler = {
        "rooms": cmd_rooms, "script": cmd_script, "search": cmd_search, "calls": cmd_calls,
        "vars": cmd_vars, "var": cmd_var, "name": cmd_name, "note": cmd_note,
    }[args.command]
    try:
        return handler(args, export)
    except BrokenPipeError:
        return 0


if __name__ == "__main__":
    sys.exit(main())
