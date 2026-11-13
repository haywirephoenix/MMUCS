#!/usr/bin/env python3
"""mroom: read the rooms of an MMUCS export in a few lines each.

Reads the scripts/ tree and, when it's there, the data/ folder that MMUCS's exporter writes
(File > Export, tick Scripts and Room data). Shares the export folder, variable names and refs index
with mscripts. Needs Python 3.9+ and nothing else. Run `mroom --help`.
"""
import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Dict, List, Optional, Tuple

TOOL_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOL_DIR.parent / "mscripts"))
import mscripts as ms  # noqa: E402

INDEX_FILE = ".mroom-index.json"
INDEX_VERSION = 3
LOCAL_SCRIPTS = 2000  # local script numbers start here; below are global

# Verbs whose use is plain from the scripts (e.g. verb 6 always describes the object). Others print as numbers.
VERBS = {5: "use-with", 6: "look", 7: "hand", 8: "mouth", 9: "walk-to", 94: "exit-cursor",
         216: "hand-text", 218: "mouth-text", 219: "use-with-text"}
CLASSES = {32: "untouchable"}

USAGE = """usage: mroom COMMAND [options]

  use [DIR]                     Remember the export to read (shared with mscripts)
  rooms                         List rooms: objects, scripts, lines, exits
  room ROOM [--all]             One room in a page: size, exits and entrances, objects, actors,
                                dialogue, scripts and the variables used there
  object OBJ                    One object: room, hover name, verbs, image, walk-to, who uses it
  exits [ROOM] [--dot]          Which rooms lead where (all rooms, or one room's in and out)
  lines [ROOM] [--actor A]      Dialogue in script order. --grep TEXT, --choices, --limit N
  actor A                       Where an actor appears, its costumes and lines
  costume N                     Where a costume is stored, which actors wear it and how they're placed
  find TEXT                     Search room names, object names, hover names and dialogue

ROOM is a number (15) or part of a name (town). OBJ is an id (397) or part of a name.
Results are written as path:line under scripts/, like mscripts. Layout (size, positions, walkboxes,
costumes, hover names) comes from data/: export with Room data ticked to get it.
"""


def out(text: str = ""):
    print(text)


# ── Reading scripts ─────────────────────────────────────────────────────────────────────────────

OBJ_HEADER = re.compile(r"object #(\d+)(?:, verbs ([\d, a-z-]+))?")
RE_DOOR = re.compile(r"^come-out-door\s+([\w-]+)")
RE_GOTO = re.compile(r"^current-room\s+(\d+)$")
RE_START = re.compile(r"^(?:start-script|start-script-quick|chain-script)\b(?:\s+[a-z]+)*\s+(\d+)\b")
RE_SAY = re.compile(r'^say-line\s+(?:([\w$-]+)\s+)?"(?:/([A-Za-z0-9_]+)/)?(.*)"$')
RE_CHOICE = re.compile(r'^verb\s+[\w-]+\s+name\s+"(?:/([A-Za-z0-9_]+)/)?(.*)"$')
RE_PUT = re.compile(r"^put-actor\s+([\w$-]+)\s+at\s+(.+?)\s+in-room\s+([\w$-]+)$")
RE_ELEVATION = re.compile(r"^actor\s+([\w$-]+)\s+elevation\s+(.+)$")
RE_DEFAULT = re.compile(r"^actor\s+([\w$-]+)\s+default$")
RE_SCALE = re.compile(r"^actor\s+([\w$-]+)\s+scale\s+(.+)$")
CONSTANT = re.compile(r"^[\d\s+\-*/()]+$")


def evaluate(expr: str) -> str:
    """'492 + 300' -> '792'; anything that isn't plain integer arithmetic stays as written."""
    expr = expr.strip()
    if CONSTANT.match(expr) and re.search(r"\d", expr):
        try:
            return str(int(eval(expr.replace("/", "//"), {"__builtins__": {}}, {})))  # digits and + - * / only
        except (SyntaxError, ZeroDivisionError, TypeError, ValueError):
            pass
    return expr


def split_args(text: str) -> List[str]:
    """Splits 'a, f(b, c), d[e, f]' on its top-level commas."""
    parts, depth, cur = [], 0, ""
    for ch in text:
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        if ch == "," and depth == 0:
            parts.append(cur)
            cur = ""
        else:
            cur += ch
    parts.append(cur)
    return [p.strip() for p in parts]
RE_COSTUME = re.compile(r"^actor\s+([\w$-]+)\s+costume\s+([\w$-]+)$")
RE_SOUND = re.compile(r"^start-sound\s+(\d+)$")
RE_VERB = re.compile(r"^    verb ([\w-]+) \{")


def scan(s: "ms.Script") -> dict:
    """The room-level facts in one script, each with its line number."""
    e: Dict[str, list] = defaultdict(list)
    # An elevation lifts where the actor is drawn. It counts for a placement when it's set after the
    # actor's last costume change (or reset) in this script, like MMUCS's own costume matching.
    elevation: Dict[str, str] = {}
    scale: Dict[str, str] = {}  # the same, for the actor's scale (255 is full size)
    for line in s.lines:
        t = line.text.strip()
        if t.startswith("}"):
            t = t.lstrip("} ").removeprefix("else").strip()
        if not t or t.startswith("//"):
            continue
        n = line.no
        if m := RE_DOOR.match(t):
            e["doors"].append([n, m.group(1)])
        elif m := RE_GOTO.match(t):
            e["goto"].append([n, int(m.group(1))])
        elif m := RE_START.match(t):
            e["starts"].append([n, int(m.group(1))])
        elif m := RE_SAY.match(t):
            e["say"].append([n, m.group(1) or "", m.group(2) or "", m.group(3)])
        elif m := RE_CHOICE.match(t):
            e["choices"].append([n, m.group(1) or "", m.group(2)])
        elif m := RE_PUT.match(t):
            xy = split_args(m.group(2))
            if len(xy) == 2:
                e["put"].append([n, m.group(1), evaluate(xy[0]), evaluate(xy[1]), m.group(3),
                                 elevation.get(m.group(1), ""), scale.get(m.group(1), "")])
        elif m := RE_COSTUME.match(t):
            e["costume"].append([n, m.group(1), m.group(2)])
            elevation.pop(m.group(1), None)
            scale.pop(m.group(1), None)
        elif m := RE_SCALE.match(t):
            scale[m.group(1)] = evaluate(m.group(2))
            e["scale"].append([n, m.group(1), scale[m.group(1)]])
        elif m := RE_ELEVATION.match(t):
            elevation[m.group(1)] = evaluate(m.group(2))
            e["elevation"].append([n, m.group(1), elevation[m.group(1)]])
        elif m := RE_DEFAULT.match(t):
            elevation.pop(m.group(1), None)
            scale.pop(m.group(1), None)
        elif m := RE_SOUND.match(t):
            e["sounds"].append([n, int(m.group(1))])
    info = {"kind": s.kind, "room": s.room, "name": s.name, "lines": len(s.lines)}
    if s.kind == "object":
        head = " ".join(s.header)
        m = OBJ_HEADER.search(head)
        stem = s.name.split("_", 1)
        if m:
            info["id"] = int(m.group(1))
            info["verbs"] = [v.strip() for v in (m.group(2) or "").split(",") if v.strip()]
        elif stem[0].isdigit():
            info["id"] = int(stem[0])
        info["object"] = stem[1] if len(stem) > 1 and stem[0].isdigit() else s.name
    info.update(e)
    return info


def verb_at(s: "ms.Script", line_no: int) -> Optional[str]:
    """The object verb a line is in, from the nearest 'verb N {' above it."""
    for line in reversed(s.lines):
        if line.no > line_no:
            continue
        m = RE_VERB.match(line.text)
        if m:
            return m.group(1)
    return None


def verb_label(v) -> str:
    try:
        n = int(v)
    except (TypeError, ValueError):
        return str(v)
    return VERBS.get(n, str(n))


# ── The game, from the export ───────────────────────────────────────────────────────────────────

class Room:
    def __init__(self, folder: str):
        self.folder = folder
        head = folder.split("_", 1)
        self.no = int(head[0]) if head[0].isdigit() else None
        self.name = head[1] if len(head) > 1 and head[0].isdigit() else folder
        self.scripts: List[str] = []  # script paths under scripts/
        self.data: Optional[dict] = None

    @property
    def label(self) -> str:
        return f"{self.no} {self.name}" if self.no is not None else self.name


class Exit:
    def __init__(self, src: str, dst: str, rel: str, line: int, how: str, via: Optional[str] = None):
        self.src, self.dst, self.rel, self.line, self.how, self.via = src, dst, rel, line, how, via


class Game:
    def __init__(self, export: "ms.Export"):
        self.export = export
        self.root: Path = export.root
        self.data_dir = self.root.parent / "data"
        self.scripts: Dict[str, dict] = self._load_index()
        self.rooms: Dict[str, Room] = {}
        for rel, info in self.scripts.items():
            if info["room"]:
                self.rooms.setdefault(info["room"], Room(info["room"])).scripts.append(rel)
        rooms_dir = self.data_dir / "rooms"
        self.has_data = rooms_dir.is_dir()
        if self.has_data:
            for p in sorted(rooms_dir.glob("*.json")):
                self.rooms.setdefault(p.stem, Room(p.stem))
        self.game: dict = {}
        if (self.data_dir / "game.json").is_file():
            self.game = json.loads((self.data_dir / "game.json").read_text(encoding="utf-8"))
        # Objects: id -> (name, room folder); name -> ids.
        self.objects: Dict[int, dict] = {}
        by_no = {r.no: r.folder for r in self.rooms.values() if r.no is not None}
        for o in self.game.get("objects", []):
            self.objects[o["id"]] = {"id": o["id"], "name": o["name"], "room": by_no.get(o["room"]),
                                     "state": o.get("state"), "classes": o.get("classes", []), "script": None}
        for rel, info in self.scripts.items():
            if info["kind"] == "object" and "id" in info:
                o = self.objects.setdefault(info["id"], {"id": info["id"], "name": info["object"], "room": None,
                                                         "state": None, "classes": [], "script": None})
                o["script"] = rel
                o["room"] = o["room"] or info["room"]
                o["verbs"] = info.get("verbs", [])
        self.object_ids: Dict[str, List[int]] = defaultdict(list)
        for o in self.objects.values():
            self.object_ids[o["name"]].append(o["id"])
        self._exits: Optional[List[Exit]] = None
        self._actor_names: Optional[Dict[str, str]] = None

    # The index: what scan() found in every script, rebuilt when the export changes.
    def _load_index(self) -> dict:
        path = self.root / INDEX_FILE
        sig = [INDEX_VERSION] + self.export.signature()
        try:
            doc = json.loads(path.read_text(encoding="utf-8"))
            if doc.get("signature") == sig:
                return doc["scripts"]
        except (OSError, ValueError, KeyError):
            pass
        scripts = {s.rel: scan(s) for s in self.export.scripts}
        try:
            path.write_text(json.dumps({"signature": sig, "scripts": scripts}, separators=(",", ":")), encoding="utf-8")
        except OSError:
            pass
        return scripts

    def room_data(self, room: Room) -> Optional[dict]:
        if room.data is None and self.has_data:
            p = self.data_dir / "rooms" / f"{room.folder}.json"
            if p.is_file():
                room.data = json.loads(p.read_text(encoding="utf-8"))
        return room.data

    def find_room(self, wanted: str) -> Room:
        wanted = wanted.strip()
        if wanted.isdigit():
            found = [r for r in self.rooms.values() if r.no == int(wanted)]
        else:
            found = [r for r in self.rooms.values() if r.name == wanted.lower()] or \
                    [r for r in self.rooms.values() if wanted.lower() in r.folder.lower()]
        if not found:
            raise SystemExit(f"mroom: no room '{wanted}'. Run `mroom rooms` for the list.")
        if len(found) > 1:
            raise SystemExit(f"mroom: '{wanted}' matches {', '.join(r.folder for r in found)}")
        return found[0]

    def room_of_object(self, ref: str) -> Optional[str]:
        """A room folder for an object written by name or id in a script."""
        ids = [int(ref)] if ref.isdigit() else self.object_ids.get(ref, [])
        for i in ids:
            o = self.objects.get(i)
            if o and o["room"]:
                return o["room"]
        return None

    def find_objects(self, wanted: str) -> List[dict]:
        wanted = wanted.strip()
        if wanted.isdigit():
            o = self.objects.get(int(wanted))
            return [o] if o else []
        exact = [self.objects[i] for i in self.object_ids.get(wanted, [])]
        if exact:
            return exact
        w = wanted.lower()
        return sorted((o for o in self.objects.values() if w in o["name"].lower()), key=lambda o: o["id"])

    def object_label(self, ref: str) -> str:
        ids = [int(ref)] if ref.isdigit() else self.object_ids.get(ref, [])
        return f"{ref} (#{ids[0]})" if ids and not ref.isdigit() else ref

    # Exits: come-out-door and current-room in a room's scripts, or in a global script that only
    # that room starts.
    @property
    def exits(self) -> List[Exit]:
        if self._exits is not None:
            return self._exits
        starters: Dict[int, set] = defaultdict(set)
        for rel, info in self.scripts.items():
            for _, n in info.get("starts", []):
                if n < LOCAL_SCRIPTS:
                    starters[n].add(info["room"] or "global")
        globals_by_no = {int(i["name"][7:11]): rel for rel, i in self.scripts.items()
                         if i["kind"] == "global" and re.fullmatch(r"script_\d{4}", i["name"])}
        result = []

        def moves(rel: str, info: dict):
            for n, target in info.get("doors", []):
                room = self.room_of_object(target)
                if room:
                    yield n, room, f"come-out-door {target}"
            for n, no in info.get("goto", []):
                room = next((r.folder for r in self.rooms.values() if r.no == no), None)
                if room:
                    yield n, room, f"current-room {no}"

        for rel, info in self.scripts.items():
            src = info["room"]
            if not src:
                continue
            for n, dst, how in moves(rel, info):
                if dst != src:
                    result.append(Exit(src, dst, rel, n, how))
            for n, g in info.get("starts", []):
                grel = globals_by_no.get(g)
                if g >= LOCAL_SCRIPTS or not grel or starters[g] != {src}:
                    continue
                for gn, dst, how in moves(grel, self.scripts[grel]):
                    if dst != src:
                        result.append(Exit(src, dst, rel, n, how, via=f"{grel}:{gn}"))
        self._exits = result
        return result

    @property
    def actor_names(self) -> Dict[Tuple[str, str], str]:
        """(room folder, actor number) -> the object its costume belongs to there, from the room data.
        Actor numbers are reused from room to room, so a name only holds in its own room. The player's
        actor (marked in the data) holds everywhere, under ("", actor)."""
        if self._actor_names is None:
            names: Dict[Tuple[str, str], str] = {}
            for room in self.rooms.values():
                d = self.room_data(room) or {}
                for c in d.get("costumes", []):
                    for p in c.get("placed", []):
                        if "actor" not in p:
                            continue  # kept in a variable: no number to name
                        a = str(p["actor"])
                        if p.get("player"):
                            names[("", a)] = "player"
                        elif c.get("object"):
                            names.setdefault((room.folder, a), c["object"])
            self._actor_names = names
        return self._actor_names

    def actor_label(self, a: str, room: Optional[str] = None) -> str:
        name = self.actor_names.get(("", a)) or (self.actor_names.get((room, a)) if room else None)
        return f"{a} ({name})" if name else a


# ── Commands ────────────────────────────────────────────────────────────────────────────────────

def cmd_use(args):
    return ms.cmd_use(args, args.root)


def cmd_rooms(args, game: Game):
    exits = defaultdict(set)
    for e in game.exits:
        exits[e.src].add(e.dst)
    out(f"{'room':<22} {'objects':>7} {'scripts':>7} {'lines':>6}  exits to")
    total_lines = 0
    for room in sorted(game.rooms.values(), key=lambda r: (r.no is None, r.no, r.folder)):
        infos = [game.scripts[r] for r in room.scripts]
        objects = sum(1 for i in infos if i["kind"] == "object")
        d = game.room_data(room)
        if d:
            objects = max(objects, sum(1 for o in d["objects"] if o.get("id") is not None))
        lines = sum(len(i.get("say", [])) for i in infos)
        total_lines += lines
        to = ", ".join(str(game.rooms[x].no) for x in sorted(exits[room.folder], key=lambda f: game.rooms[f].no or 0))
        out(f"{room.label:<22} {objects:>7} {len(room.scripts):>7} {lines:>6}  {to}")
    out(f"\n{len(game.rooms)} rooms, {total_lines} lines of dialogue in rooms" +
        ("" if game.has_data else ". No room data: export with Room data ticked for sizes, positions and hover names"))
    return 0


def describe_exit(game: Game, e: Exit, other: str, here: Optional[str] = None) -> str:
    """One exit as a line; paths in the room `here` are written relative to its folder."""
    s = game.export.load(e.rel)
    where = e.rel.split("/", 2)[-1] if here and e.rel.startswith(f"rooms/{here}/") else e.rel
    verb = verb_at(s, e.line) if s and game.scripts[e.rel]["kind"] == "object" else None
    parts = [f"{game.rooms[other].label:<18}", f"{e.how}"]
    src = f"{where}:{e.line}"
    if verb:
        src += f" (verb {verb_label(verb)})"
    parts.append(f"from {src}")
    if e.via:
        parts.append(f"via {e.via}")
    return "  ".join(parts)


def room_vars(game: Game, room: Room) -> Tuple[Counter, Counter]:
    """Variables used in a room's scripts: refs per variable, and refs that write."""
    rels = set(room.scripts)
    index = game.export.index
    ids = {i for i, rel in enumerate(index.scripts) if rel in rels}
    refs, writes = Counter(), Counter()
    for key, flat in index.refs.items():
        for i in range(0, len(flat), 2):
            if flat[i] in ids:
                refs[key] += 1
                if flat[i + 1] < 0:
                    writes[key] += 1
    return refs, writes


def object_row(game: Game, o: dict, script_info: Optional[dict]) -> str:
    verbs = (o.get("verbs") or (script_info or {}).get("verbs") or [])
    vtext = " ".join(verb_label(v) for v in verbs)
    title = f'"{o["title"]}"' if o.get("title") else ""
    img = o.get("image")
    at = f'{img["x"]},{img["y"]} {img["width"]}x{img["height"]}' if img else ""
    flags = " ".join(CLASSES.get(c, "") for c in o.get("classes", []) if c in CLASSES)
    oid = "" if o.get("id") is None else str(o["id"])
    return f"  {oid:>4} {o['name']:<30} {title:<22} {at:<18} {vtext}{('  [' + flags + ']') if flags else ''}"


def cmd_room(args, game: Game):
    room = game.find_room(args.room)
    d = game.room_data(room)
    infos = {rel: game.scripts[rel] for rel in room.scripts}
    limit = None if args.all else args.top

    head = f"Room {room.label}"
    if d:
        head += f"   {d['width']}x{d['height']}, {d['palettes']} palette{'s' if d['palettes'] != 1 else ''}, " \
                f"{d['sounds']} sounds, {len(d['boxes'])} walkboxes  ({d['file']})"
    out(head)
    kinds = Counter(i["kind"] for i in infos.values())
    names = sorted(i["name"] for i in infos.values() if i["kind"] == "room")
    local = [n for n in names if n.startswith("local_")]
    out(f"scripts/rooms/{room.folder}: {', '.join(n for n in names if not n.startswith('local_'))}"
        f"{', ' if names and len(local) < len(names) else ''}{len(local)} local scripts, {kinds['object']} object scripts")
    if not d:
        out("(no room data: export with Room data ticked for size, positions, hover names and costumes)")

    # Exits and entrances
    outs = [e for e in game.exits if e.src == room.folder]
    ins = [e for e in game.exits if e.dst == room.folder]
    out(f"\nExits ({len({e.dst for e in outs})} rooms)")
    ref = room.no if room.no is not None else room.folder
    for dst, group in sorted(group_by(outs, lambda e: e.dst).items(), key=lambda kv: game.rooms[kv[0]].no or 0):
        group.sort(key=lambda e: (e.via is not None, e.rel, e.line))
        more = f"  (+{len(group) - 1}, mroom exits {ref})" if len(group) > 1 else ""
        out("  → " + describe_exit(game, group[0], dst, room.folder) + more)
    out(f"Entrances ({len({e.src for e in ins})} rooms)")
    for src, group in sorted(group_by(ins, lambda e: e.src).items(), key=lambda kv: game.rooms[kv[0]].no or 0):
        group.sort(key=lambda e: (e.via is not None, e.rel, e.line))
        more = f"  (+{len(group) - 1})" if len(group) > 1 else ""
        out("  ← " + describe_exit(game, group[0], src) + more)

    # Objects
    objs = list(d["objects"]) if d else []
    if not objs:
        for rel, i in infos.items():
            if i["kind"] == "object":
                objs.append({"id": i.get("id"), "name": i["object"], "verbs": i.get("verbs", [])})
    by_id = {i.get("id"): i for i in infos.values() if i["kind"] == "object"}
    objs.sort(key=lambda o: (o.get("id") is None, o.get("id") or 0))
    out(f"\nObjects ({len(objs)})   id name                           hover name             at                 verbs")
    for o in objs[:limit]:
        out(object_row(game, o, by_id.get(o.get("id"))))
    if limit and len(objs) > limit:
        out(f"  … {len(objs) - limit} more (--all)")

    # Actors
    actors: Dict[str, dict] = defaultdict(lambda: {"costumes": set(), "put": [], "lines": 0})
    for rel, i in infos.items():
        for n, a, c in i.get("costume", []):
            if c != "0":  # costume 0 takes the costume off
                actors[a]["costumes"].add(c)
        for n, a, x, y, r, el, sc in i.get("put", []):
            if r in ("current-room", str(room.no)):
                actors[a]["put"].append(f"{place_text(x, y, el, sc)} ({rel.split('/', 2)[-1]}:{n})")
        for s in i.get("say", []):
            actors[s[1] or "?"]["lines"] += 1
    for rel, i in game.scripts.items():
        if i["room"] != room.folder:
            for n, a, x, y, r, el, sc in i.get("put", []):
                if r == str(room.no):
                    actors[a]["put"].append(f"{place_text(x, y, el, sc)} ({rel}:{n})")
    if d:
        for c in d.get("costumes", []):
            for a in [p["actor"] for p in c.get("placed", []) if "actor" in p] + c.get("worn_by", []):
                actors[str(a)]["costumes"].add(str(c["costume"]))
    numbered = {a: v for a, v in actors.items() if not a.startswith("local") and a != "?"}
    out(f"\nActors ({len(numbered)})")
    for a in sorted(numbered, key=lambda a: (not a.isdigit(), int(a) if a.isdigit() else 0, a))[:limit]:
        v = numbered[a]
        bits = []
        if v["costumes"]:
            bits.append("costumes " + ", ".join(sorted(v["costumes"], key=lambda c: (not c.isdigit(), int(c) if c.isdigit() else 0, c))))
        if v["lines"]:
            bits.append(f"{v['lines']} lines")
        if v["put"]:
            bits.append("put at " + v["put"][0] + (f" (+{len(v['put']) - 1})" if len(v["put"]) > 1 else ""))
        out(f"  {game.actor_label(a, room.folder):<22} {';  '.join(bits)}")

    # Dialogue
    say = [(rel, s) for rel, i in infos.items() for s in i.get("say", [])]
    choices = sum(len(i.get("choices", [])) for i in infos.values())
    by_actor = Counter(s[1] for _, s in say)
    out(f"\nDialogue: {len(say)} lines" + (f", {choices} dialogue choices" if choices else "") +
        (f"   ({', '.join(f'{game.actor_label(a, room.folder)}: {n}' for a, n in by_actor.most_common(5))})" if say else "") +
        f"   mroom lines {room.no if room.no is not None else room.folder}")
    shown = [(rel, s) for rel, s in say if s[3] and not s[3].startswith("%")][:3 if limit else None]
    for rel, s in shown:
        out(f"  {rel}:{s[0]}  {game.actor_label(s[1], room.folder)}: {s[3]}")

    # Scripts started from here
    started = Counter()
    for i in infos.values():
        for _, n in i.get("starts", []):
            if n < LOCAL_SCRIPTS:
                started[n] += 1
    if started:
        items = [f"{n}" + (f"x{c}" if c > 1 else "") for n, c in sorted(started.items())]
        out(f"\nGlobal scripts started here: {', '.join(items[:limit * 2] if limit else items)}" +
            (" …" if limit and len(items) > limit * 2 else ""))
    sounds = sorted({n for i in infos.values() for _, n in i.get("sounds", [])})
    if sounds:
        out(f"Sounds started: {', '.join(map(str, sounds[:limit * 2] if limit else sounds))}" +
            (" …" if limit and len(sounds) > limit * 2 else ""))

    # Variables
    refs, writes = room_vars(game, room)
    names = game.export.names
    status = Counter()
    unnamed = []
    for key in refs:
        kind, n = ("var", int(key[3:])) if key.startswith("var") else ("bit", int(key[3:]))
        st = names.status(kind, n) or "unnamed"
        status[st] += 1
        if st in ("unnamed", "tracked"):
            unnamed.append((refs[key], key, st))
    unnamed.sort(reverse=True)
    out(f"\nVariables: {len(refs)} used here: " + ", ".join(f"{c} {s}" for s, c in status.most_common()))
    if unnamed:
        out("  not named: " + ", ".join(f"{k}{'?' if st == 'tracked' else ''} ({c})" for c, k, st in unnamed[:limit or None]) +
            ("  …" if limit and len(unnamed) > limit else ""))
        out(f"  mscripts progress --left {room.no if room.no is not None else room.folder}")
    named_here = sorted(((refs[k], k) for k in refs if names.name_of(*split_key(k))), reverse=True)
    if named_here:
        out("  named: " + ", ".join(f"{names.name_of(*split_key(k))} ({c})" for c, k in named_here[:limit or None]) +
            ("  …" if limit and len(named_here) > limit else ""))
    return 0


def place_text(x: str, y: str, elevation: str, scale: str = "") -> str:
    """'320,792 elevation 300 (drawn at 320,492)', with ', scale 150' when it isn't full size."""
    text = f"{x},{y}"
    if elevation and elevation != "0":
        text += f" elevation {elevation} (drawn at {x},{evaluate(f'{y} - {elevation}')})"
    if scale and scale != "255":
        text += f", scale {scale}"
    return text


def cmd_costume(args, game: Game):
    """Where a costume is kept, which actors wear it, and how the scripts place them."""
    c = str(args.costume)
    home = None
    for room in game.rooms.values():
        for entry in (game.room_data(room) or {}).get("costumes", []):
            if str(entry.get("costume")) == c:
                home = (room, entry)
    if home:
        room, entry = home
        out(f"Costume {c}: stored in room {room.label} (AKOS {entry['akos']})" +
            (f", belongs to {entry['object']}" if entry.get("object") else ""))
        for p in entry.get("placed", []):
            el, sc = str(p.get("elevation", "")), str(p.get("scale", ""))
            who = f"actor {p['actor']}" if "actor" in p else "an actor kept in a variable"
            by = f", z-plane {p['zplane']}" if p.get("zplane") else ""
            by += f", dressed by script {p['dressed_by']}" if "dressed_by" in p else ""
            out(f"  MMUCS places it: {who} at {place_text(str(p.get('x', '?')), str(p.get('y', '?')), el, sc)}{by}")
        worn = entry.get("worn_by", [])
        if worn:
            who = f"actor{'s' if len(worn) > 1 else ''} {', '.join(map(str, worn))}"
            out(f"  worn by {who}, placed at runtime (the script's position isn't a constant)")
        if entry.get("dressed_by") is not None and not entry.get("placed"):
            out(f"  offered by script {entry['dressed_by']}, which a script here starts, but placed nowhere MMUCS can read")
        elif not entry.get("placed") and not entry.get("worn_by"):
            out("  MMUCS found no script that dresses an actor in it with a constant")
    elif game.has_data:
        out(f"Costume {c}: not in any room's data")
    else:
        out(f"Costume {c}: no room data, so where it's stored isn't known")

    # Each script that dresses an actor in it, and what that script does with the actor next.
    wearers = [(rel, i, n, a) for rel, i in sorted(game.scripts.items()) for n, a, cc in i.get("costume", []) if cc == c]
    if not wearers:
        out("No script sets it with a literal number.")
        return 0
    out(f"\nWorn in {len(wearers)} places:")
    for rel, i, n, a in wearers:
        later = [x for x, act, cc in i.get("costume", []) if act == a and x > n]
        until = min(later) if later else 10 ** 9
        out(f"  {rel}:{n}  actor {game.actor_label(a, i['room'])} costume {c}")
        if re.fullmatch(r"local\d+", a) and i["kind"] == "global":
            out(f"    (actor {a} is passed in by whoever starts it: mscripts calls {int(i['name'][7:11])})")
        sets = [(k, f"elevation {v}") for k, act, v in i.get("elevation", []) if act == a and n < k < until]
        sets += [(k, f"scale {v}") for k, act, v in i.get("scale", []) if act == a and n < k < until]
        for k, text in sorted(sets):
            out(f"    :{k}  {text}")
        puts = [p for p in i.get("put", []) if p[1] == a and n < p[0] < until]
        for k, act, x, y, r, el, sc in puts[:args.limit]:
            out(f"    :{k}  put-actor at {place_text(x, y, el, sc)} in-room {r}")
        if len(puts) > args.limit:
            out(f"    … {len(puts) - args.limit} more")
        if not puts:
            out("    (not placed in this script after the costume is set)")
    # The same actors placed by the other scripts of the costume's room.
    if home:
        room = home[0]
        actors = sorted({a for _, _, _, a in wearers})
        moves = []
        for rel in sorted(room.scripts):
            if any(rel == w[0] for w in wearers):
                continue
            for k, act, x, y, r, el, sc in game.scripts[rel].get("put", []):
                if act in actors:
                    moves.append(f"  {rel}:{k}  put-actor {act} at {place_text(x, y, el, sc)} in-room {r}")
        if moves:
            out(f"\nThe same actors placed by other scripts in {room.label}:")
            for line in moves[:args.limit]:
                out(line)
            if len(moves) > args.limit:
                out(f"  … {len(moves) - args.limit} more")
        pattern = "|".join(actors)
        out(f'\nMovement and animation: mscripts search "(actor|walk-actor|do-animation) ({pattern})\\b" --in {room.no}')
    return 0


def split_key(key: str) -> Tuple[str, int]:
    return (key[:3], int(key[3:]))


def group_by(items, key):
    g = defaultdict(list)
    for x in items:
        g[key(x)].append(x)
    return g


def cmd_object(args, game: Game):
    found = game.find_objects(args.obj)
    if not found:
        raise SystemExit(f"mroom: no object '{args.obj}'")
    if len(found) > 1 and not args.obj.isdigit():
        out(f"{len(found)} objects match '{args.obj}':")
        for o in found[:args.limit]:
            room = game.rooms.get(o["room"])
            out(f"  {o['id']:>4} {o['name']:<34} {room.label if room else ''}")
        if len(found) > args.limit:
            out(f"  … {len(found) - args.limit} more")
        return 0
    o = found[0]
    room = game.rooms.get(o["room"]) if o["room"] else None
    data_obj = None
    if room and (d := game.room_data(room)):
        data_obj = next((x for x in d["objects"] if x.get("id") == o["id"]), None)
    out(f"Object {o['id']} {o['name']}" + (f'   hover name "{data_obj["title"]}"' if data_obj and data_obj.get("title") else ""))
    out(f"room: {room.label if room else 'none (inventory or not placed)'}")
    if o.get("state") is not None:
        cls = ", ".join(f"{c}{' ' + CLASSES[c] if c in CLASSES else ''}" for c in o.get("classes", []))
        out(f"starts in state {o['state']}" + (f", classes {cls}" if cls else ""))
    img = (data_obj or {}).get("image")
    if img:
        walk = "  walk-to " + " / ".join(f"{x},{y}" for x, y in img["walk_to"]) if img.get("walk_to") else ""
        out(f"image: {img['x']},{img['y']} {img['width']}x{img['height']}, {img['states']} states, "
            f"actor faces {img.get('actor_dir', 0)}°{walk}")
    if o.get("script"):
        s = game.export.load(o["script"])
        out(f"script: {o['script']}")
        verbs = []
        for line in s.lines:
            m = RE_VERB.match(line.text)
            if m:
                verbs.append(f"{verb_label(m.group(1))}:{line.no}")
        if verbs:
            out("verbs: " + "  ".join(verbs))
        info = game.scripts[o["script"]]
        for n, target in info.get("doors", []):
            r = game.room_of_object(target)
            out(f"leads to: {game.rooms[r].label if r else '?'} (come-out-door {target}, :{n})")
        for _, a, code, text in info.get("say", [])[:args.lines]:
            out(f'  says: {game.actor_label(a, info["room"])}: {text}')
    # Doors that lead out of this object, and where scripts name it.
    arrivals = [(rel, n) for rel, i in game.scripts.items() for n, t in i.get("doors", []) if t == o["name"] or t == str(o["id"])]
    if arrivals:
        out(f"arrive here from: " + ", ".join(f"{rel}:{n}" for rel, n in arrivals[:args.limit]))
    pat = re.compile(ms.alias_pattern([o["name"]]))
    uses = []
    for s in game.export.scripts:
        if s.rel == o.get("script"):
            continue
        hits = [line for line in s.lines if pat.search(line.text)]
        if hits:
            uses.append((s, hits))
    out(f"used in {len(uses)} other scripts" + (":" if uses else ""))
    for s, hits in uses[:args.limit]:
        out(f"  {s.rel}:{hits[0].no}  {hits[0].text.strip()[:90]}" + (f"  (+{len(hits) - 1})" if len(hits) > 1 else ""))
    if len(uses) > args.limit:
        out(f"  … {len(uses) - args.limit} more (--limit)")
    return 0


def cmd_exits(args, game: Game):
    if args.dot:
        out("digraph rooms {")
        pairs = sorted({(e.src, e.dst) for e in game.exits})
        for src, dst in pairs:
            out(f'  "{game.rooms[src].label}" -> "{game.rooms[dst].label}";')
        out("}")
        return 0
    if args.room:
        room = game.find_room(args.room)
        for e in sorted((e for e in game.exits if e.src == room.folder), key=lambda e: game.rooms[e.dst].no or 0):
            out("→ " + describe_exit(game, e, e.dst))
        for e in sorted((e for e in game.exits if e.dst == room.folder), key=lambda e: game.rooms[e.src].no or 0):
            out("← " + describe_exit(game, e, e.src))
        return 0
    by_src = group_by(game.exits, lambda e: e.src)
    for src in sorted(by_src, key=lambda f: game.rooms[f].no or 0):
        dsts = sorted({e.dst for e in by_src[src]}, key=lambda f: game.rooms[f].no or 0)
        out(f"{game.rooms[src].label:<20} → {', '.join(game.rooms[d].label for d in dsts)}")
    no_exit = [r for r in game.rooms.values() if r.folder not in by_src]
    out(f"\n{len(by_src)} rooms with exits; none found for {len(no_exit)}: " +
        ", ".join(str(r.no) for r in sorted(no_exit, key=lambda r: r.no or 0)))
    return 0


def cmd_lines(args, game: Game):
    if args.room:
        room = game.find_room(args.room)
        rels = sorted(room.scripts)
    else:
        rels = sorted(game.scripts)
    grep = re.compile(args.grep, re.I) if args.grep else None
    count = 0
    for rel in rels:
        i = game.scripts[rel]
        rows = []
        if not args.choices:
            rows += [(s[0], game.actor_label(s[1] or "?", i["room"]), s[3]) for s in i.get("say", [])
                     if args.actor is None or s[1] == args.actor]
        if args.choices or args.actor is None:
            rows += [(c[0], "choice", c[2]) for c in i.get("choices", [])]
        for n, who, text in sorted(rows):
            if grep and not grep.search(text):
                continue
            count += 1
            if count <= args.limit:
                out(f"{rel}:{n}  {who}: {text}")
    if count > args.limit:
        out(f"… {count - args.limit} more (--limit)")
    elif count == 0:
        out("No lines.")
    return 0


def cmd_actor(args, game: Game):
    a = args.actor
    rooms = defaultdict(lambda: {"lines": 0, "costumes": set(), "put": 0})
    samples = []
    for rel, i in game.scripts.items():
        where = i["room"] or "global"
        for s in i.get("say", []):
            if s[1] == a:
                rooms[where]["lines"] += 1
                if s[3] and not s[3].startswith("%") and len(samples) < args.lines:
                    samples.append(f"{rel}:{s[0]}  {s[3]}")
        for _, x, c in i.get("costume", []):
            if x == a and c != "0":
                rooms[where]["costumes"].add(c)
        for _, x, _, _, r, *_ in i.get("put", []):
            if x == a:
                target = where if r == "current-room" else next((f for f in game.rooms if game.rooms[f].no == (int(r) if r.isdigit() else -1)), where)
                rooms[target]["put"] += 1
    out(f"Actor {game.actor_label(a)}: in {len([r for r in rooms if r != 'global'])} rooms, "
        f"{sum(v['lines'] for v in rooms.values())} lines")
    for r in sorted(rooms, key=lambda f: (f == "global", game.rooms[f].no if f in game.rooms else 0)):
        v = rooms[r]
        label = game.rooms[r].label if r in game.rooms else r
        bits = [f"{v['lines']} lines"] if v["lines"] else []
        if v["costumes"]:
            bits.append("costumes " + ", ".join(sorted(v["costumes"])))
        if v["put"]:
            bits.append(f"put {v['put']}x")
        out(f"  {label:<20} {';  '.join(bits)}")
    for s in samples:
        out("  " + s)
    return 0


def cmd_find(args, game: Game):
    w = args.text.lower()
    rooms = [r for r in game.rooms.values() if w in r.folder.lower()]
    objs = [o for o in game.objects.values() if w in o["name"].lower()]
    titled = []
    for r in game.rooms.values():
        for o in (game.room_data(r) or {}).get("objects", []):
            if o.get("title") and w in o["title"].lower():
                titled.append((r, o))
    lines = [(rel, s) for rel, i in game.scripts.items() for s in i.get("say", []) if w in s[3].lower()]
    if rooms:
        out("Rooms: " + ", ".join(r.label for r in sorted(rooms, key=lambda r: r.no or 0)))
    if objs:
        out(f"Objects ({len(objs)}):")
        for o in sorted(objs, key=lambda o: o["id"])[:args.limit]:
            r = game.rooms.get(o["room"])
            out(f"  {o['id']:>4} {o['name']:<34} {r.label if r else ''}")
    if titled:
        out(f"Hover names ({len(titled)}):")
        for r, o in titled[:args.limit]:
            out(f"  {o.get('id') or '':>4} {o['name']:<34} \"{o['title']}\"  {r.label}")
    if lines:
        out(f"Lines ({len(lines)}):")
        for rel, s in lines[:args.limit]:
            out(f"  {rel}:{s[0]}  {game.actor_label(s[1], game.scripts[rel]['room'])}: {s[3]}")
    if not (rooms or objs or titled or lines):
        out("Nothing found.")
    return 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="mroom", add_help=False)
    p.add_argument("-h", "--help", action="store_true")
    p.add_argument("--root", help="the export folder (holding scripts/ and data/)")
    sub = p.add_subparsers(dest="command")
    s = sub.add_parser("use")
    s.add_argument("dir", nargs="?")
    sub.add_parser("rooms")
    s = sub.add_parser("room")
    s.add_argument("room")
    s.add_argument("--all", action="store_true", help="list everything")
    s.add_argument("--top", type=int, default=12, help="rows per section (default 12)")
    s = sub.add_parser("object")
    s.add_argument("obj")
    s.add_argument("--limit", type=int, default=15)
    s.add_argument("--lines", type=int, default=3, help="sample lines from its script")
    s = sub.add_parser("exits")
    s.add_argument("room", nargs="?")
    s.add_argument("--dot", action="store_true", help="Graphviz output")
    s = sub.add_parser("lines")
    s.add_argument("room", nargs="?")
    s.add_argument("--actor")
    s.add_argument("--grep")
    s.add_argument("--choices", action="store_true", help="only dialogue choices")
    s.add_argument("--limit", type=int, default=200)
    s = sub.add_parser("actor")
    s.add_argument("actor")
    s.add_argument("--lines", type=int, default=5)
    s = sub.add_parser("costume")
    s.add_argument("costume", type=int)
    s.add_argument("--limit", type=int, default=8)
    s = sub.add_parser("find")
    s.add_argument("text")
    s.add_argument("--limit", type=int, default=20)
    return p


def main(argv: Optional[List[str]] = None) -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except AttributeError:
        pass
    args = build_parser().parse_args(argv)
    if args.help or not args.command:
        out(USAGE.rstrip())
        return 0
    if args.command == "use":
        return cmd_use(args)
    scripts_root = ms.find_scripts_root(args.root)
    game = Game(ms.Export(scripts_root, ms.REPO_MAP if ms.REPO_MAP.exists() else None))
    handler = {"rooms": cmd_rooms, "room": cmd_room, "object": cmd_object, "exits": cmd_exits,
               "lines": cmd_lines, "actor": cmd_actor, "costume": cmd_costume, "find": cmd_find}[args.command]
    try:
        return handler(args, game)
    except BrokenPipeError:
        return 0


if __name__ == "__main__":
    sys.exit(main())
