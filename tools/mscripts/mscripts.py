#!/usr/bin/env python3
"""mscripts: search an MMUCS script export and gather evidence for naming variables.

Reads the scripts/ tree that MMUCS's exporter writes (File > Export, Scripts). Needs Python 3.9+
and nothing else. Run `mscripts --help`, or `mscripts COMMAND --help` for a command's options.
"""
import argparse
import json
import os
import re
import subprocess
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
  script WHERE...               Print a script: "global 6", "9 local_2006", "9 entry", a path.
                                Part of one: --lines A-B, --at N [-C K], --block N [--levels K]
  search REGEX [--in ROOM]...   Find lines, with -C for context and --count per script
  calls N [--in ROOM]           List who starts, calls or chains to script N

Name variables:
  vars [--unnamed] [--top N]    Rank variables by how many scripts use them, with their status
  progress [--left [ROOM]]      How many variables are named, noted and left, and where the rest are
  var V                         Evidence for one variable: writes, comparisons, commands, rooms
  refs V [--writes] [-C N]      Every line using a variable (from the index), --by-room for counts
  name V NAME --note TEXT       Name a variable in variables.json (checks the name first)
  note V TEXT                   Track a variable in variables.json without naming it
  review [REF] [--all]          Check changed names: their notes' citations, against the evidence

V is a number (266), varN, bitN or a name (item-icon). The export is found from --root,
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
        # The names the exported files use: what the refs index reads them with.
        self.file_by_name: Dict[str, Tuple[str, int]] = {}
        for n, name in self.globals.items():
            self.file_by_name.setdefault(name, ("var", n))
        for n, name in self.bits.items():
            self.file_by_name.setdefault(name, ("bit", n))
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

    def number_in_file(self, word: str) -> Optional[Tuple[str, int]]:
        """Like number_of_word, but only with the names the export's files use (when it lists them)."""
        if not self.has_index:
            return self.number_of_word(word)
        m = NUMBERED.match(word)
        if m:
            return None if m.group(1) == "local" else (m.group(1), int(m.group(2)))
        return self.file_by_name.get(word)


def room_of(rel: str) -> Optional[str]:
    parts = rel.split("/")
    return parts[1] if parts[0] == "rooms" and len(parts) > 2 else None


WRITE_AFTER = re.compile(r"(?:\[(?:[^\[\]]|\[[^\]]*\])*\])*\s*(?:=(?!=)|(?:\+\+|--)$)")
INDEX_FILE = ".mscripts-index.json"
INDEX_VERSION = 2


class RefIndex:
    """Where every variable is used: for each varN/bitN, the (script, line) of every reference and
    whether the line writes it. Built from the whole export once and saved beside it, then rebuilt
    only when the export changes."""

    def __init__(self, scripts: List[str], refs: Dict[str, List[int]]):
        self.scripts = scripts  # script paths, by id
        self.refs = refs        # "var266" -> [script id, line, ...]; a negative line writes it

    @staticmethod
    def build(export: "Export") -> "RefIndex":
        names = export.names
        rels, refs = [], defaultdict(list)
        for sid, s in enumerate(export.scripts):
            rels.append(s.rel)
            for line in s.lines:
                t = line.text.strip()
                seen = set()
                for m in WORD.finditer(t):
                    key = names.number_in_file(m.group(0))
                    if not key:
                        continue
                    k = f"{key[0]}{key[1]}"
                    wr = m.start() == 0 and k not in seen and WRITE_AFTER.match(t, m.end())
                    refs[k] += [sid, -line.no if wr else line.no]
                    seen.add(k)
        return RefIndex(rels, dict(refs))

    def places(self, kind: str, n: int) -> List[Tuple[str, int, bool]]:
        """Every reference as (script, line, writes it); a line using it twice is listed twice."""
        flat = self.refs.get(f"{kind}{n}", [])
        return [(self.scripts[flat[i]], abs(flat[i + 1]), flat[i + 1] < 0) for i in range(0, len(flat), 2)]

    def keys(self, kind: str) -> List[int]:
        return [int(k[len(kind):]) for k in self.refs if k.startswith(kind) and k[len(kind):].isdigit()]


class Export:
    def __init__(self, scripts_root: Path, map_path: Optional[Path]):
        self.root = scripts_root
        self.names = Names(scripts_root, map_path)
        self._scripts: Optional[List[Script]] = None
        self._loaded: Dict[str, Script] = {}
        self._paths: Optional[List[Path]] = None
        self._index: Optional[RefIndex] = None

    @property
    def paths(self) -> List[Path]:
        if self._paths is None:
            self._paths = sorted(self.root.rglob("*.scumm"))
            if not self._paths:
                raise SystemExit(f"mscripts: no .scumm files under {self.root}")
        return self._paths

    @property
    def scripts(self) -> List[Script]:
        if self._scripts is None:
            self._scripts = [self._loaded.get(p.relative_to(self.root).as_posix()) or Script(self.root, p) for p in self.paths]
            self._loaded = {s.rel: s for s in self._scripts}
        return self._scripts

    def load(self, rel: str) -> Optional[Script]:
        """One script by its path under scripts/, read only when it's needed."""
        if rel not in self._loaded:
            p = self.root / rel
            if not p.is_file():
                return None
            self._loaded[rel] = Script(self.root, p)
        return self._loaded[rel]

    def signature(self) -> List:
        """Changes whenever a script is added, removed or rewritten, or the export's names change."""
        count = size = latest = 0
        for p in self.paths:
            st = p.stat()
            count, size, latest = count + 1, size + st.st_size, max(latest, st.st_mtime_ns)
        names = self.root / "variables.index.json"
        if names.exists():
            extra = names.stat().st_mtime_ns
        else:  # names come from the map instead
            mp = self.names.map_path
            extra = mp.stat().st_mtime_ns if mp and mp.exists() else 0
        return [INDEX_VERSION, count, size, latest, extra]

    @property
    def index(self) -> RefIndex:
        if self._index is None:
            path = self.root / INDEX_FILE
            sig = self.signature()
            try:
                doc = json.loads(path.read_text(encoding="utf-8"))
                if doc.get("signature") == sig:
                    self._index = RefIndex(doc["scripts"], doc["refs"])
            except (OSError, ValueError, KeyError):
                pass
            if self._index is None:
                self._index = RefIndex.build(self)
                try:
                    path.write_text(json.dumps({"signature": sig, "scripts": self._index.scripts, "refs": self._index.refs},
                                               separators=(",", ":")), encoding="utf-8")
                except OSError:
                    pass  # a read-only export: work without saving it
        return self._index

    def scripts_using(self, kind: str, n: int) -> List[Script]:
        rels = dict.fromkeys(rel for rel, _, _ in self.index.places(kind, n))
        return [s for s in (self.load(r) for r in rels) if s]

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
    write = re.compile(rf"^{a}((?:\[(?:[^\[\]]|\[[^\]]*\])*\])*)\s*=(?!=)\s*(.*)$")
    incdec = re.compile(rf"^{a}((?:\[(?:[^\[\]]|\[[^\]]*\])*\])*)\s*(\+\+|--)$")  # the decompiler's var++ / var--
    cmp_after = re.compile(rf"{a}\s*(==|!=|<=|>=|<|>)\s*(-?\w[\w-]*)")
    cmp_before = re.compile(rf"(-?\w[\w-]*)\s*(==|!=|<=|>=|<|>)\s*{a}")
    in_list = re.compile(rf"{a}\s+in\s+\[([^\]]*)\]")
    copy = re.compile(rf"^([\w$-]+(?:\[(?:[^\[\]]|\[[^\]]*\])*\])*)\s*=\s*{a}$")
    assign = re.compile(r"^([\w$-]+)(?:\[(?:[^\[\]]|\[[^\]]*\])*\])*\s*=(?!=)")
    flip = {"<": ">", ">": "<", "<=": ">=", ">=": "<="}
    u = Uses()
    for s in export.scripts_using(kind, n):
        for line in s.lines:
            t = line.text.strip()
            hits = len(var.findall(t))
            if not hits:
                continue
            u.refs += hits
            u.scripts.add(s.rel)
            u.rooms[s.room or "global"] += 1
            u.lines.append((s, line))
            m = incdec.match(t)
            if m:
                u.writes["+ 1 (increment)" if m.group(2) == "++" else "- 1 (decrement)"].append((s, line))
                continue
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
    lines, why = s.lines, ""
    if args.lines or args.at is not None or args.block is not None:
        if not s.lines:
            raise SystemExit(f"mscripts: {s.rel} has no code lines")
        if args.lines:
            m = re.fullmatch(r"(\d+)(?:-(\d+))?", args.lines.strip())
            if not m:
                raise SystemExit("mscripts: --lines takes N or A-B")
            lo, hi = int(m.group(1)), int(m.group(2) or m.group(1))
        elif args.at is not None:
            lo, hi = args.at - args.context, args.at + args.context
        else:
            lo, hi, why = enclosing_block(s, args.block, args.levels)
        lines = [l for l in s.lines if lo <= l.no <= hi]
        if not lines:
            raise SystemExit(f"mscripts: {s.rel} has no lines {lo}-{hi} (its code is {s.lines[0].no}-{s.lines[-1].no})")
        out(f"// {s.rel}:{lines[0].no}-{lines[-1].no} of {s.lines[0].no}-{s.lines[-1].no}" + (f"  ({why})" if why else ""))
    else:
        for h in s.header:
            out(h)
        out(f"// {s.rel}")
        out()
    for line in lines:
        out(line.text if args.plain else f"{line.no:5}  {line.text}")
    return 0


STRING = re.compile(r'"(?:[^"\\]|\\.)*"')


def enclosing_block(s: Script, line_no: int, levels: int = 1) -> Tuple[int, int, str]:
    """The lines of the brace block around line_no, `levels` blocks out: (first, last, what it is)."""
    if not any(l.no == line_no for l in s.lines):
        raise SystemExit(f"mscripts: {s.rel} has no line {line_no} (its code is {s.lines[0].no}-{s.lines[-1].no})")
    pairs, stack = [], []
    for l in s.lines:
        for ch in re.findall(r"[{}]", STRING.sub("", l.text)):
            if ch == "{":
                stack.append(l.no)
            elif stack:
                pairs.append((stack.pop(), l.no))
    around = sorted((p for p in pairs if p[0] <= line_no <= p[1]), key=lambda p: p[1] - p[0])
    if len(around) < max(1, levels):
        return s.lines[0].no, s.lines[-1].no, "the whole script: no block that far out"
    lo, hi = around[max(1, levels) - 1]
    return lo, hi, f"the block around line {line_no}" + (f", {levels} levels out" if levels > 1 else "")


def room_matches_rel(rel: str, wanted: str) -> bool:
    room = room_of(rel)
    if room is None:
        return False
    if wanted.isdigit():
        return room[:3].isdigit() and int(room.split("_")[0]) == int(wanted)
    return wanted.lower() in room.lower()


def cmd_refs(args, export: Export):
    names = export.names
    kind, n = names.resolve(args.var)
    places = export.index.places(kind, n)
    if args.writes:
        places = [p for p in places if p[2]]
    if args.only_global:
        places = [p for p in places if not room_of(p[0])]
    if args.room:
        places = [p for p in places if any(room_matches_rel(p[0], r) for r in args.room)]
    name = names.name_of(kind, n)
    title = f"{kind}{n}" + (f"  {name}" if name else "")
    if not places:
        out(f"{title}: no {'writes' if args.writes else 'references'}" + (" there." if args.room or args.only_global else "."))
        return 0
    # One row per line, marked as a write if any reference on it writes.
    by_script: Dict[str, Dict[int, bool]] = {}
    for rel, no, wr in places:
        rows = by_script.setdefault(rel, {})
        rows[no] = rows.get(no, False) or wr
    nlines = sum(len(v) for v in by_script.values())
    nwrites = sum(1 for rows in by_script.values() for w in rows.values() if w)
    if args.by_room or args.count:
        per, wcount = Counter(), Counter()
        for rel, rows in by_script.items():
            key = (room_of(rel) or "global") if args.by_room else rel
            per[key] += len(rows)
            wcount[key] += sum(1 for w in rows.values() if w)
        out(f"{'lines':>6} {'writes':>6}  {'room' if args.by_room else 'script'}")
        for key, c in per.most_common():
            out(f"{c:6} {wcount[key]:6}  {key}")
    else:
        shown = 0
        for rel, rows in by_script.items():
            s = export.load(rel)
            if s is None:
                continue
            at = {l.no: i for i, l in enumerate(s.lines)}
            hits = sorted(at[no] for no in rows if no in at)
            hit_set, done = set(hits), set()
            for i in hits:
                lo, hi = max(0, i - args.context), min(len(s.lines), i + args.context + 1)
                if args.context and done and lo > max(done) + 1:
                    out("  --")
                for j in range(lo, hi):
                    if j in done:
                        continue
                    done.add(j)
                    mark = ":" if j in hit_set else "-"
                    out(f"{s.rel}{mark}{s.lines[j].no}{mark} {s.lines[j].text.strip()}")
                shown += 1
                if args.limit and shown >= args.limit:
                    out(f"... stopped at {args.limit} lines (--limit)")
                    return 0
    out(f"\n{title}: {nlines} lines in {len(by_script)} scripts" + ("" if args.writes else f", {nwrites} writing it"))
    return 0


STATES = ["engine", "named", "unconfirmed", "unclear", "noted", "untouched"]


def entry_state(names: Names, kind: str, n: int, doc: Optional[dict] = None) -> str:
    """engine, named, unconfirmed, unclear, noted (another kind of note) or untouched.
    With doc, reads that map (an older variables.json) instead of the current one."""
    if kind == "var" and n in names.engine:
        return "engine"
    e = ((doc or names.map)[names.section(kind)].get(str(n))) or {}
    if e.get("Name") or (doc is None and names.name_of(kind, n)):
        return "named"
    note = (e.get("Note") or "").lstrip().lower()
    if note.startswith("unconfirmed"):
        return "unconfirmed"
    if note.startswith("unclear"):
        return "unclear"
    return "noted" if note else "untouched"


def usage_group(rooms: set) -> str:
    """Where a variable is used: its room, "global scripts only" or "several rooms"."""
    rs = rooms - {"global"}
    if len(rs) == 1:
        return next(iter(rs))
    return "global scripts only" if not rs else "several rooms"


def cmd_progress(args, export: Export):
    names = export.names
    kind = "bit" if args.bits else "var"
    refs, scripts, writes, rooms = var_usage(export, kind)
    used = sorted(refs)
    state = {n: entry_state(names, kind, n) for n in used}
    totals = Counter(state.values())
    out(f"{len(used)} {'bits' if args.bits else 'globals'} used in scripts")
    for st in STATES:
        if totals[st] or st in ("named", "untouched"):
            out(f"  {st:<12} {totals[st]:5}  {100 * totals[st] / max(1, len(used)):5.1f}%")
    notes = totals["unconfirmed"] + totals["unclear"] + totals["noted"]
    out(f"  done (named or engine) {totals['named'] + totals['engine']}, notes only {notes}, left {totals['untouched']}")

    if names.map_path and names.map_path.exists() and not args.no_git:
        try:
            base = map_at_ref(names.map_path, args.ref) or {}
        except SystemExit:
            base = None
        if base is not None:
            base.setdefault("Globals", {})
            base.setdefault("Bits", {})
            moved = Counter(state[n] for n in used if entry_state(names, kind, n, base) != state[n])
            out(f"  since {args.ref}: " + (", ".join(f"{c} now {st}" for st, c in moved.most_common()) if moved else "no change"))

    groups: Dict[str, Counter] = defaultdict(Counter)
    for n in used:
        g = usage_group(rooms[n])
        groups[g][state[n]] += 1
        if state[n] == "untouched" and "global" in rooms[n] and len(rooms[n]) == 2:
            groups[g]["also-global"] += 1
    rows = [(g, c) for g, c in groups.items() if args.all or c["untouched"]]
    rows.sort(key=lambda r: (-r[1]["untouched"], r[0]))
    out(f"\n{'left':>5} {'notes':>5} {'done':>5} {'total':>5}  where they're used")
    for g, c in rows[:args.top]:
        total = sum(c[st] for st in STATES)
        also = c["also-global"]
        out(f"{c['untouched']:5} {c['unconfirmed'] + c['unclear'] + c['noted']:5} {c['named'] + c['engine']:5} {total:5}  {g}"
            + (f"  ({also} of those left also in global scripts)" if also else ""))
    if len(rows) > args.top:
        out(f"  ... {len(rows) - args.top} more (--top)")
    out("A room's row counts the variables no other room uses; global scripts may use them too.")

    if args.left is not None:
        want = args.left
        out()
        for n in used:
            if state[n] != "untouched":
                continue
            g = usage_group(rooms[n])
            rs = rooms[n] - {"global"}
            if want and not (want.lower() in g.lower() or
                             (want.isdigit() and len(rs) == 1 and room_matches_rel(f"rooms/{next(iter(rs))}/x", want))):
                continue
            out(f"  {kind + str(n):<8} {len(scripts[n]):4} scripts {refs[n]:5} refs {writes[n]:4} writes  {g}"
                + (" + global scripts" if "global" in rooms[n] and len(rs) == 1 else ""))
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


def var_usage(export: Export, kind: str):
    """From the index: per variable, its refs, scripts, rooms and writes."""
    refs, scripts, writes, rooms = Counter(), defaultdict(set), Counter(), defaultdict(set)
    for n in export.index.keys(kind):
        for rel, _, wr in export.index.places(kind, n):
            refs[n] += 1
            scripts[n].add(rel)
            rooms[n].add(room_of(rel) or "global")
            if wr:
                writes[n] += 1
    return refs, scripts, writes, rooms


def cmd_vars(args, export: Export):
    names = export.names
    want = "bit" if args.bits else "var"
    refs, scripts, writes, rooms = var_usage(export, want)
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
        return "names are lower-case kebab-case, each part starting with a letter (e.g. item-icon, timer2)"
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



# ── Review ──────────────────────────────────────────────────────────────────────────────────────

# A citation in a note: global/script_0039.scumm:61, rooms/009_cannon/local_2006.scumm:44, or :109-110.
SCRIPT_PATH = re.compile(r"(?:scripts/)?((?:global|rooms)/[\w./-]+?\.scumm)")
CITATION = re.compile(r"(?:(?:scripts/)?((?:global|rooms)/[\w./-]+?\.scumm)|(?<![\w/:]))"
                      r":(\d+)(?:-(\d+))?((?:/\d+)*)(?![\w])")


def map_at_ref(path: Path, ref: str) -> Optional[dict]:
    """The map file as it was at git REF, or None when it didn't exist there."""
    try:
        top = subprocess.run(["git", "-C", str(path.parent), "rev-parse", "--show-toplevel"],
                             capture_output=True, text=True, check=True).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        raise SystemExit(f"mscripts: {path} isn't in a git repository; use --all to review every entry")
    rel = path.resolve().relative_to(Path(top).resolve()).as_posix()
    r = subprocess.run(["git", "-C", top, "show", f"{ref}:{rel}"], capture_output=True, encoding="utf-8")
    if r.returncode != 0:
        if "exists on disk, but not in" in r.stderr or "does not exist in" in r.stderr:
            return None
        raise SystemExit(f"mscripts: git couldn't read {rel} at {ref}: {r.stderr.strip()}")
    return json.loads(r.stdout)


def closed_before(text: str, pos: int, end: int) -> bool:
    """True when pos is inside a parenthesis that closes before end."""
    depth = 0
    for i in range(pos - 1, -1, -1):  # the innermost unmatched '(' before pos
        if text[i] == ")":
            depth += 1
        elif text[i] == "(":
            if depth == 0:
                break
            depth -= 1
    else:
        return False  # not inside a parenthesis
    depth = 0
    for j in range(i + 1, len(text)):
        if text[j] == "(":
            depth += 1
        elif text[j] == ")":
            if depth == 0:
                return j < end
            depth -= 1
    return False


def citations(note: str) -> List[Tuple[str, str, List[Tuple[int, int]], int]]:
    """The path:line citations in a note, as (text, script, [(first, last)], position).

    Understands path:61, path:109-110 and path:5/58/124, and a bare :112 after one, meaning the
    same script again."""
    found, last = [], None
    # A script named without a line ("global/script_0040.scumm (…): = local1 at :118") sets the
    # script a bare :N refers to, as much as one cited with a line does.
    named = [(m.start(), m.group(1)) for m in SCRIPT_PATH.finditer(note)]
    for m in CITATION.finditer(note):
        rel = m.group(1)
        before = [(p, r) for p, r in named if p < m.start()]
        if rel is None and before:
            # Skip scripts named inside a parenthesis that closed before this citation, as a reader
            # would: "script_0040.scumm (… script_0001.scumm:193): … at :118" means script 40.
            # A bare citation in parentheses itself, "(… :124-125)", continues the latest script named.
            in_parens = closed_before(note, m.start(), len(note) + 1)
            open_here = [r for p, r in before if in_parens or not closed_before(note, p, m.start())]
            last = (open_here or [r for _, r in before])[-1]
        if rel is None:
            if last is None:
                continue  # a bare :N with no script before it (a time, a ratio)
            rel = last
        last = rel
        spans = [(int(m.group(2)), int(m.group(3) or m.group(2)))]
        spans += [(int(x), int(x)) for x in re.findall(r"\d+", m.group(4) or "")]
        found.append((m.group(0).strip(), rel, spans, m.start()))
    return found


def check_citations(export: Export, note: str, aliases: List[str]) -> List[Tuple[str, str]]:
    """Each citation in a note, as (status, text). "wrong": the script or line doesn't exist.
    "check": the line exists but doesn't mention the variable, which may be fine (it shows context)
    but is worth reading. "ok": it mentions the variable, or another variable the note cites it for."""
    own = re.compile(alias_pattern(aliases))
    results = []
    for text, rel, spans, pos in citations(note):
        sc = export.load(rel)
        if sc is None:
            results.append(("wrong", f"{text}  no such script ({rel})"))
            continue
        lines = {l.no: l for l in sc.lines}
        span = [lines[i] for a, b in spans for i in range(a, b + 1) if i in lines]
        missing = [a for a, b in spans if not any(i in lines for i in range(a, b + 1))]
        if missing:
            have = f"lines {sc.lines[0].no}-{sc.lines[-1].no}" if sc.lines else "no lines"
            results.append(("wrong", f"{text}  {rel} has no line {missing[0]} (its code is {have})"))
            continue
        if any(own.search(l.text) for l in span):
            results.append(("ok", f"{text}"))
            continue
        # Cited for another variable the note names just before it ("var595 (= 330, path:line)").
        before = note[max(0, pos - 80):pos]
        others = [w for w in WORD.findall(before) if export.names.number_of_word(w) and w not in aliases]
        if others and any(re.search(alias_pattern([o]), l.text) for o in others for l in span):
            results.append(("ok", f"{text}  (about {others[-1]})"))
            continue
        results.append(("check", f"{text}  doesn't mention it: {span[0].text.strip()[:70]}"))
    return results


def evidence_lines(u: "Uses") -> List[str]:
    """The evidence in a few lines, to read beside a note's claims."""
    def top(counter: Counter, k: int = 4) -> str:
        return ", ".join(f"{key} x{c}" for key, c in counter.most_common(k))
    out_lines = [f"{u.refs} refs in {len(u.scripts)} scripts, {len([r for r in u.rooms if r != 'global'])} rooms"]
    writes = Counter({form: len(p) for form, p in u.writes.items()})
    if u.constants:
        values = sorted(u.constants)
        label = f"= {values[0]}" if len(values) == 1 else f"= constants {values[0]}..{values[-1]} ({len(values)} values)"
        writes[label] = sum(len(p) for p in u.constants.values())
    writers = sorted({sc.rel for p in list(u.writes.values()) + list(u.constants.values()) for sc, _ in p})
    if writes:
        out_lines.append(f"writes: {top(writes, 3)}")
        out_lines.append(f"written in: {', '.join(writers)}" if len(writers) <= 4 else f"written in {len(writers)} scripts")
    else:
        out_lines.append("never written by a script")
    for title, counter in (("compared", u.compares), ("commands", u.commands), ("calls", u.calls), ("copied into", u.copied_into)):
        if counter:
            out_lines.append(f"{title}: {top(counter)}")
    if u.tests:
        out_lines.append(f"tested true/false x{len(u.tests)}")
    return out_lines


def cmd_review(args, export: Export):
    names = export.names
    path = export.names.map_path
    if path is None or not path.exists():
        raise SystemExit("mscripts: no variables.json to review (pass --map FILE)")
    doc = load_map(path)
    base = {"Globals": {}, "Bits": {}} if args.all else (map_at_ref(path, args.ref) or {"Globals": {}, "Bits": {}})
    base.setdefault("Globals", {})
    base.setdefault("Bits", {})

    entries = []
    for section, kind in (("Globals", "var"), ("Bits", "bit")):
        for key, e in doc[section].items():
            if not key.isdigit():
                continue
            e = e or {}
            old = base[section].get(key)
            if not args.all and old == e:
                continue
            entries.append((kind, int(key), e, old or {}))
    if not entries:
        out(f"No variable names changed since {args.ref}." if not args.all else "The map is empty.")
        return 0

    problems = 0
    no_citations = []
    totals = Counter()
    for kind, n, e, old in sorted(entries, key=lambda t: (t[0], t[1])):
        name, old_name = e.get("Name") or "", old.get("Name") or ""
        if name and old_name and name != old_name:
            change = f"{old_name} -> {name}  (renamed)"
        elif name and not old_name:
            change = f"{name}  (new)"
        elif name:
            change = f"{name}  (note changed)"
        else:
            change = "unnamed" + ("  (name removed: " + old_name + ")" if old_name else "")
        out(f"{kind}{n}  {change}")
        note = e.get("Note") or ""
        out(f"  note: {note}" if note else "  note: (none)")
        issue = check_name(names, doc, kind, n, name) if name else None
        if issue:
            out(f"  ! name: {issue}")
            problems += 1
        aliases = sorted(set(names.aliases(kind, n) + [x for x in (name, old_name) if x]))
        for line in evidence_lines(collect(export, kind, n)):
            out(f"  | {line}")
        cites = check_citations(export, note, aliases)
        if not cites:
            no_citations.append(f"{kind}{n}")
            out("  ? cites no path:line")
        for status, text in cites:
            totals[status] += 1
            if status == "wrong":
                problems += 1
            if status != "ok" or args.verbose:
                out(f"  {'ok   ' if status == 'ok' else 'WRONG' if status == 'wrong' else 'check'} {text}")
        out()

    out(f"{len(entries)} entries; citations: {totals['ok']} ok, {totals['check']} to check, {totals['wrong']} wrong"
        + (f"; no citations: {', '.join(no_citations)}" if no_citations else ""))
    out("ok: mentions the variable. check: exists but doesn't mention it (may be context). wrong: no such script or line.")
    if not names.has_index:
        out("This export has no variables.index.json, so engine-variable checks were skipped. Re-export from a current MMUCS.")
    return 1 if problems else 0

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
    part = s.add_mutually_exclusive_group()
    part.add_argument("--lines", help="only lines N or A-B")
    part.add_argument("--at", type=int, help="only the lines around line N (see -C)")
    part.add_argument("--block", type=int, help="only the brace block around line N")
    s.add_argument("-C", "--context", type=int, default=10, help="lines either side for --at (default 10)")
    s.add_argument("--levels", type=int, default=1, help="for --block: how many blocks out (default 1)")

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

    s = sub.add_parser("refs", help="every line using a variable")
    s.add_argument("var")
    s.add_argument("--writes", action="store_true", help="only lines that write it")
    s.add_argument("--in", dest="room", action="append", default=[], help="room number or part of its name (repeatable)")
    s.add_argument("--global", dest="only_global", action="store_true", help="only global scripts")
    s.add_argument("-C", "--context", type=int, default=0, help="lines of context")
    s.add_argument("--by-room", action="store_true", help="lines and writes per room")
    s.add_argument("--count", action="store_true", help="lines and writes per script")
    s.add_argument("--limit", type=int, default=0, help="stop after N lines")
    s.add_argument("--map", help="variables.json to read names from")

    s = sub.add_parser("progress", help="how many variables are done and left")
    s.add_argument("--bits", action="store_true", help="bit variables instead of globals")
    s.add_argument("--all", action="store_true", help="list every room, not just those with variables left")
    s.add_argument("--top", type=int, default=30, help="rows of rooms (default 30)")
    s.add_argument("--left", nargs="?", const="", help="list the variables left: all, or in ROOM (number or part of a name, "
                                                    "'global' or 'several')")
    s.add_argument("--ref", default="HEAD", help="git ref to compare with (default HEAD)")
    s.add_argument("--no-git", action="store_true", help="skip the comparison with git")
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
    s = sub.add_parser("review", help="check changed names against the evidence")
    s.add_argument("ref", nargs="?", default="HEAD", help="git ref to compare variables.json with (default HEAD)")
    s.add_argument("--all", action="store_true", help="review every entry, not just changed ones")
    s.add_argument("-v", "--verbose", action="store_true", help="list good citations too")
    s.add_argument("--map", help="variables.json to review (default: the repo's)")
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
        "vars": cmd_vars, "var": cmd_var, "refs": cmd_refs, "progress": cmd_progress, "name": cmd_name, "note": cmd_note, "review": cmd_review,
    }[args.command]
    try:
        return handler(args, export)
    except BrokenPipeError:
        return 0


if __name__ == "__main__":
    sys.exit(main())
