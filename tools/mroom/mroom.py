#!/usr/bin/env python3
"""mroom: read the rooms of an MMUCS export in a few lines each.

Reads the scripts/ tree and, when it's there, the data/ folder that MMUCS's exporter writes
(File > Export, tick Scripts and Room data). Shares the export folder, variable names and refs index
with mscripts. Needs Python 3.9+ and nothing else. Run `mroom --help`.
"""
import argparse
import json
import os
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
  start ROOM                    What the room viewer starts with: costumes shown and hidden as the room
                                opens (the player, cutscene, later, another costume), with their lines
  review [N] [--changed]        Costumes and objects marked in MMUCS (Mark for review…): what changed
                                since marking, and everything about one. --tag T, --done N clears one
  compare OTHER [ROOM]          Every costume placement that differs between another export (OTHER, e.g.
                                one made before a change to MMUCS) and this one: position, elevation, scale,
                                z-plane, draw order; and whether the picture moved or only its depth. --all
                                lists unchanged rooms too
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
            by += {"entry": ", on entry", "entry-if": ", on entry (conditional)", "cutscene": ", in a cutscene",
                   "later": ", later"}.get(p.get("when"), "")
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


# ── Review marks (Mark for review… in MMUCS's room viewer) ────────────────────────────────────────

SNAPSHOT_FIELDS = [("placed", "placed"), ("actor", "actor"), ("x", "x"), ("y", "y"), ("width", "width"), ("height", "height"),
                   ("elevation", "elevation"), ("scale", "scale"), ("zplane", "z-plane"), ("dressed_by", "dressed by"),
                   ("owner", "object"), ("entry_state", "state on entry"), ("when", "when placed"),
                   ("starts_shown", "shown on start")]
OBJECT_ONLY = ("width", "height", "entry_state")
COSTUME_ONLY = ("when", "starts_shown")
DEFAULTS = {"elevation": 0, "zplane": 0, "scale": 255, "starts_shown": False}  # the export leaves these out


def live_marks_path() -> Path:
    """Where MMUCS keeps the marks: review.json beside its settings."""
    if os.name == "nt" and os.environ.get("LOCALAPPDATA"):
        return Path(os.environ["LOCALAPPDATA"]) / "MMUCS" / "review.json"
    return Path.home() / ".local" / "share" / "MMUCS" / "review.json"


def marks_file(game: "Game", arg: Optional[str]) -> Optional[Path]:
    """--marks FILE, else MMUCS's live file, else the copy the export made."""
    for p in ([Path(arg)] if arg else [live_marks_path(), game.data_dir / "review.json"]):
        if p.is_file():
            return p
    return None


def load_marks(path: Optional[Path]) -> List[dict]:
    if not path:
        return []
    try:
        marks = json.loads(path.read_text(encoding="utf-8")).get("marks", [])
    except (OSError, ValueError):
        raise SystemExit(f"mroom: can't read the review marks in {path}")
    for m in marks:  # a first-format mark held its one target in its own fields
        if "targets" not in m:
            m["targets"] = [{k: m.get(k) for k in ("kind", "costume", "akos", "object", "snapshot")}]
    return marks


def current_of(game: "Game", room_no: int, target: dict) -> Tuple[Optional[dict], str]:
    """What the export says now about a target, in the snapshot's terms, or why it can't say."""
    room = next((r for r in game.rooms.values() if r.no == room_no), None)
    d = game.room_data(room) if room else None
    if not d:
        return None, "no room data for this room in the export"
    snap = target.get("snapshot") or {}
    if target.get("kind") == "object":
        o = next((o for o in d["objects"] if o.get("name") == target.get("object") and o.get("image")), None) \
            or next((o for o in d["objects"] if o.get("name") == target.get("object")), None)
        if not o:
            return None, "the object isn't in the room data"
        img = o.get("image") or {}
        return {"placed": bool(img.get("states")), "x": img.get("x"), "y": img.get("y"), "width": img.get("width"),
                "height": img.get("height"), "entry_state": o.get("entry_state")}, "object"
    entry = next((c for c in d["costumes"] if c.get("akos") == target.get("akos")), None) \
        or next((c for c in d["costumes"] if c.get("costume") == target.get("costume")), None)
    if not entry:
        return None, "the costume isn't in the room data"
    placed = entry.get("placed", [])
    if not placed:
        cur = {"placed": False, "owner": entry.get("object"), "starts_shown": False}
        if entry.get("dressed_by") is not None:
            cur["dressed_by"] = entry["dressed_by"]
        return cur, "costume"
    # The placement at the marked spot, else the nearest (one costume can stand in several places).
    sx, sy = snap.get("x"), snap.get("y")
    p = min(placed, key=lambda q: 0 if sx is None else abs(q.get("x", 0) - sx) + abs(q.get("y", 0) - (sy or 0)))
    return {"placed": True, "when": p.get("when"), "starts_shown": bool(p.get("starts_shown")),
            "actor": p.get("actor"), "x": p.get("x"), "y": p.get("y"), "elevation": p.get("elevation"),
            "scale": p.get("scale", 255), "zplane": p.get("zplane", 0), "dressed_by": p.get("dressed_by"),
            "owner": entry.get("object"), **({"sort_y": p["sort_y"]} if "sort_y" in p else {})}, "costume"


def fields_of(target: dict, cur: Optional[dict]) -> List[str]:
    snap = target.get("snapshot") or {}
    return [k for k, _ in SNAPSHOT_FIELDS if (k in snap or (cur and k in cur))
            and not (k in OBJECT_ONLY and target.get("kind") != "object")
            and not (k in COSTUME_ONLY and (target.get("kind") == "object" or k not in snap))]  # marks made before these were recorded


def target_changes(target: dict, cur: Optional[dict]) -> List[Tuple[str, object, object]]:
    if cur is None:
        return []
    snap = target.get("snapshot") or {}
    labels = dict(SNAPSHOT_FIELDS)
    changes = []
    for key in fields_of(target, cur):
        then, now = snap.get(key), cur.get(key)
        d = DEFAULTS.get(key)
        if (then if then is not None else d) != (now if now is not None else d):
            changes.append((labels[key], then, now))
    return changes


def target_label(target: dict) -> str:
    if target.get("kind") == "object":
        return f"object {target.get('object')}"
    snap = target.get("snapshot") or {}
    at = f" at {snap['x']},{snap['y']}" if snap.get("placed") and snap.get("x") is not None else " (not placed)"
    return f"costume {target.get('costume')}{at}"


def mark_label(mark: dict) -> str:
    return " + ".join(target_label(t) for t in mark["targets"])


def save_marks(path: Path, marks: List[dict]):
    doc = json.loads(path.read_text(encoding="utf-8"))
    doc["marks"] = marks
    tmp = path.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(doc, indent=2, ensure_ascii=False), encoding="utf-8")
    tmp.replace(path)


def mark_status(game: "Game", mark: dict) -> Tuple[str, List[str]]:
    """'same', 'changed' (with what), or '?' when the export can't say."""
    changed, unknown = [], False
    many = len(mark["targets"]) > 1
    for t in mark["targets"]:
        cur, _ = current_of(game, mark["room"], t)
        unknown |= cur is None
        for label, _, _ in target_changes(t, cur):
            changed.append(f"{target_label(t)} {label}" if many else label)
    if many and order_of(game, mark, now=False) != order_of(game, mark, now=True):
        changed.append("draw order")
    return ("changed" if changed else "?" if unknown else "same"), changed


def order_of(game: "Game", mark: dict, now: bool) -> List[int]:
    """The mark's placed costumes in draw order (y before elevation, then actor), as target indexes."""
    rows = []
    for i, t in enumerate(mark["targets"]):
        if t.get("kind") != "costume":
            continue
        v = current_of(game, mark["room"], t)[0] if now else (t.get("snapshot") or {})
        if v and v.get("placed") and v.get("y") is not None:
            rows.append((v.get("sort_y", v["y"]), v.get("actor") or 0, i))
    return [i for _, _, i in sorted(rows)]


def draw_order(game: "Game", room: "Room", mark: dict):
    """The room's placed costumes in the order the engine draws them, with the mark's own pointed out."""
    d = game.room_data(room) or {}
    rows = []
    for c in d.get("costumes", []):
        for p in c.get("placed", []):
            rows.append((p.get("sort_y", p.get("y", 0)), p.get("actor") or 0, c, p))
    rows.sort(key=lambda r: (r[0], r[1]))
    if not rows:
        return

    def is_mine(c, p):
        for t in mark["targets"]:
            snap = t.get("snapshot") or {}
            if t.get("kind") == "costume" and c.get("akos") == t.get("akos") and \
                    (snap.get("x") is None or (p.get("x"), p.get("y")) == (snap.get("x"), snap.get("y"))):
                return True
        return False

    mine = [i for i, (_, _, c, p) in enumerate(rows) if is_mine(c, p)]
    if len(rows) <= 20 or not mine:
        lo, hi = 0, len(rows)
    else:
        lo, hi = max(0, min(mine) - 4), min(len(rows), max(mine) + 5)
    out(f"\nDraw order in {room.label} (behind first; {len(rows)} placed){'' if (lo, hi) == (0, len(rows)) else f', {lo + 1}-{hi} shown'}:")
    for i in range(lo, hi):
        y, actor, c, p = rows[i]
        who = f"actor {actor}" if actor else (f"dressed by {p['dressed_by']}" if "dressed_by" in p else "actor ?")
        extra = "".join([f" elevation {p['elevation']}" if p.get("elevation") else "", f" scale {p['scale']}" if "scale" in p else "",
                         f" z-plane {p['zplane']}" if p.get("zplane") else "",
                         f" (in front of actor{'s' if len(p['removes']) > 1 else ''} {', '.join(map(str, p['removes']))}, which its script takes out)"
                         if p.get("removes") and "sort_y" in p else ""])
        out(f"  {'→' if i in mine else ' '} y {y:>4}  costume {c.get('costume')!s:>4}  {who:<18} at {p.get('x')},{p.get('y')}{extra}"
            + (f"  ({c['object']})" if c.get("object") else ""))


# ── Comparing two exports (before and after a change to MMUCS) ──────────────────────────────────

# Placement fields compared, with the value an absent one means.
PLACEMENT_FIELDS = [("x", None), ("y", None), ("elevation", 0), ("scale", 255), ("zplane", 0), ("sort_y", None), ("removes", None)]


def placements_of(room_json: dict) -> Dict[tuple, dict]:
    """A room's placements keyed by (costume, AKOS, actor, which of that costume's spots)."""
    found = {}
    for c in room_json.get("costumes", []):
        for i, p in enumerate(c.get("placed", [])):
            found[(c.get("costume"), c.get("akos"), p.get("actor"), i)] = p
    return found


def placement_value(p: dict, field: str, default):
    if field == "sort_y":
        return p.get("sort_y", p.get("y"))
    return p.get(field, default)


def compare_placement(before: Optional[dict], after: Optional[dict]) -> Tuple[List[str], bool]:
    """What changed ('y -6 → 474'), and whether the picture moved (its drawn spot or size), not just its depth."""
    if before is None or after is None:
        return (["new"] if before is None else ["gone"]), True
    changes = []
    for field, default in PLACEMENT_FIELDS:
        a, b = placement_value(before, field, default), placement_value(after, field, default)
        if a != b and not (field == "sort_y" and before.get("sort_y") is None and after.get("sort_y") is None):
            label = {"zplane": "z-plane", "sort_y": "sort y"}.get(field, field)
            changes.append(f"{label} {a if a is not None else '-'} → {b if b is not None else '-'}")
    def drawn(p):
        return (p.get("x"), (p.get("y") or 0) - (p.get("elevation") or 0), p.get("scale", 255))
    return changes, drawn(before) != drawn(after)


def other_rooms_dir(arg: str) -> Path:
    """An export folder (with data/rooms), its data folder, or the rooms folder itself."""
    base = Path(arg)
    for d in (base / "data" / "rooms", base / "rooms", base):
        if d.is_dir() and any(d.glob("*.json")):
            return d
    raise SystemExit(f"mroom: no room data in {arg} (export it with Room data ticked, or mprobe export --data)")


def cmd_compare(args, game: "Game"):
    if not game.has_data:
        raise SystemExit("mroom: this export has no data/ folder to compare")
    other = other_rooms_dir(args.other)
    mine = game.data_dir / "rooms"
    names = sorted({p.name for p in other.glob("*.json")} | {p.name for p in mine.glob("*.json")})
    if args.room:
        names = [f"{game.find_room(args.room).folder}.json"]
    total = moved = rooms = 0
    for name in names:
        a = json.loads((other / name).read_text(encoding="utf-8")) if (other / name).is_file() else {}
        b = json.loads((mine / name).read_text(encoding="utf-8")) if (mine / name).is_file() else {}
        pa, pb = placements_of(a), placements_of(b)
        rows = []
        for key in sorted(set(pa) | set(pb), key=lambda k: (k[0] or 0, k[2] or 0, k[3])):
            if pa.get(key) == pb.get(key):
                continue
            changes, picture = compare_placement(pa.get(key), pb.get(key))
            if not changes:
                continue
            p = pb.get(key) or pa.get(key)
            who = f"actor {key[2]}" if key[2] else (f"dressed by {p['dressed_by']}" if "dressed_by" in p else "actor ?")
            rows.append(f"  costume {key[0]!s:>4}  {who:<16} at {p.get('x')},{p.get('y')}: {', '.join(changes)}"
                        + ("" if picture else "  (same picture)"))
            moved += picture
        if rows or args.all:
            out(f"{Path(name).stem}" + ("" if rows else ": same"))
            for r in rows:
                out(r)
        total += len(rows)
        rooms += bool(rows)
    out(f"\n{total} placement{'s' if total != 1 else ''} changed in {rooms} room{'s' if rooms != 1 else ''}"
        f": {moved} drawn somewhere else, {total - moved} only in depth.")
    return 0


def cmd_review(args, game: "Game"):
    path = marks_file(game, args.marks)
    marks = load_marks(path)
    if args.done is not None:
        if not path or (path != live_marks_path() and not args.marks):
            raise SystemExit("mroom: clear marks in MMUCS's file; this export's copy is read-only")
        kept = [m for m in marks if m.get("id") != args.done]
        if len(kept) == len(marks):
            raise SystemExit(f"mroom: no mark #{args.done}")
        save_marks(path, kept)
        out(f"Cleared mark #{args.done} in {path}")
        return 0
    if not marks:
        out("No review marks. In MMUCS's room viewer, right-click a costume or object: Mark for review…")
        return 0
    if args.id is not None:
        mark = next((m for m in marks if m.get("id") == args.id), None)
        if not mark:
            raise SystemExit(f"mroom: no mark #{args.id}")
        return review_one(game, mark)

    rows = []
    for m in marks:
        if args.tag and args.tag not in m.get("tags", []):
            continue
        status, changes = mark_status(game, m)
        if args.changed and status != "changed":
            continue
        rows.append((m, status, changes))
    out(f"{len(rows)} review mark{'s' if len(rows) != 1 else ''} ({path}):")
    for m, status, changes in rows:
        room = next((r.label for r in game.rooms.values() if r.no == m["room"]), str(m["room"]))
        tags = ",".join(m.get("tags", [])) or "-"
        out(f"  #{m['id']:<3} {room:<16} {mark_label(m)}")
        out(f"        {tags}  {status}" + (f": {', '.join(changes)}" if changes else "") + (f"  — {m['note']}" if m.get("note") else ""))
    out("\nmroom review N for one in full; --changed for those MMUCS now works out differently; --done N to clear one.")
    return 0


def review_one(game: "Game", mark: dict):
    room = next((r for r in game.rooms.values() if r.no == mark.get("room")), None)
    targets = mark["targets"]
    out(f"Mark #{mark['id']} in room {room.label if room else mark.get('room')}: {mark_label(mark)}  (marked {mark.get('created', '?')})")
    if mark.get("tags"):
        out(f"looks wrong: {', '.join(mark['tags'])}")
    if mark.get("note"):
        out(f"note: {mark['note']}")
    labels = dict(SNAPSHOT_FIELDS)
    for t in targets:
        cur, how = current_of(game, mark["room"], t)
        snap = t.get("snapshot") or {}
        out(f"\n{target_label(t)}" + ("" if len(targets) > 1 else ""))
        if cur is None:
            out(f"  now: {how}")
            continue
        changes = {c[0] for c in target_changes(t, cur)}
        out("  when marked → in this export:" if changes else "  MMUCS still works it out the same:")
        for key in fields_of(t, cur):
            then, now = snap.get(key), cur.get(key)
            out(f"    {labels[key]:<15} {then!s:<24}{('→ ' + str(now)) if labels[key] in changes else ''}")

    if len(targets) > 1:
        then, now = order_of(game, mark, now=False), order_of(game, mark, now=True)
        if len(then) > 1 or len(now) > 1:
            out("\nBetween them (drawn behind first: by y before elevation, then actor; a z-plane cuts what's behind it):")

            def describe(order, use_now):
                parts = []
                for i in order:
                    t = targets[i]
                    v = current_of(game, mark["room"], t)[0] if use_now else (t.get("snapshot") or {})
                    parts.append(f"costume {t.get('costume')} (y {v.get('y')}, actor {v.get('actor') or '?'}, z-plane {v.get('zplane') or 0})")
                return "  →  ".join(parts)

            out(f"  when marked: {describe(then, False)}")
            out(f"  now:         {describe(now, True)}" + ("" if then == now else "   (order changed)"))
    if not room:
        return 0
    for t in targets:
        if t.get("kind") == "costume" and t.get("costume") is not None:
            out("")
            cmd_costume(argparse.Namespace(costume=t["costume"], limit=8), game)
        elif t.get("kind") == "object":
            out("")
            cmd_object(argparse.Namespace(obj=t["object"], limit=10, lines=3), game)
            pat = re.compile(rf"^state-of {re.escape(t['object'])} is ")
            sets = [(s.rel, line.no, line.text.strip()) for s in game.export.scripts for line in s.lines if pat.match(line.text.strip())]
            out(f"\nScripts that set its state ({len(sets)}):")
            for rel, n, text in sets[:12]:
                out(f"  {rel}:{n}  {text}")
    if "visibility" in mark.get("tags", []) and room:
        start_section(game, room, [t for t in targets if t.get("kind") == "costume"])
    if any(t.get("kind") == "costume" for t in targets):
        draw_order(game, room, mark)
    return 0

# ── What a room starts with (the room viewer's starting state) ────────────────────────────────────

WHEN_TEXT = {"entry": "on entry", "entry-if": "on entry (conditional)", "cutscene": "in a cutscene", "later": "later"}


def start_rows(game: "Game", room: "Room") -> List[dict]:
    """Every placed costume in the room with whether it starts shown, why not, and the line that places it."""
    d = game.room_data(room) or {}
    puts = [(rel, put) for rel in room.scripts for put in game.scripts[rel].get("put", [])]
    rows = []
    for c in d.get("costumes", []):
        for p in c.get("placed", []):
            actor = p.get("actor")
            if p.get("starts_shown"):
                why = "shown"
            elif p.get("player"):
                why = "the player"
            elif p.get("when") in ("cutscene", "later"):
                why = WHEN_TEXT[p["when"]]
            elif p.get("when") in ("entry", "entry-if"):
                why = f"another costume of actor {actor} starts" if actor else "another costume starts here"
            else:
                why = "not worked out"
            # The script line that places it: a put-actor of the same actor at the same spot (by
            # position when the actor is in a variable). Several can match; the first is shown.
            x, y = str(p.get("x")), str(p.get("y"))
            line = next((f"{rel}:{put[0]}" for rel, put in puts
                         if (actor is None or put[1] == str(actor)) and put[2] == x and put[3] == y), None)
            if line is None and actor is not None:
                line = next((f"{rel}:{put[0]}" for rel, put in puts if put[1] == str(actor)), None)
            rows.append({"costume": c.get("costume"), "akos": c.get("akos"), "object": c.get("object"), "p": p,
                         "shown": bool(p.get("starts_shown")), "why": why, "line": line})
    return rows


def start_row_text(r: dict) -> str:
    p = r["p"]
    who = f"actor {p['actor']}" if p.get("actor") else (f"dressed by {p['dressed_by']}" if "dressed_by" in p else "actor ?")
    when = WHEN_TEXT.get(p.get("when"), "?")
    owner = f"  ({r['object']})" if r.get("object") else ""
    reason = "" if r["shown"] else f"  hidden: {r['why']}"
    return f"costume {r['costume']!s:>4}  {who:<18} at {p.get('x')},{p.get('y')}  {when}{reason}{owner}" + \
        (f"\n           {r['line']}" if r.get("line") else "")


def start_section(game: "Game", room: "Room", targets: List[dict]):
    """For a visibility mark: each marked costume's start state, and why."""
    rows = start_rows(game, room)
    out("\nAs the room opens:")
    for t in targets:
        snap = t.get("snapshot") or {}
        mine = [r for r in rows if r["akos"] == t.get("akos")
                and (snap.get("x") is None or (r["p"].get("x"), r["p"].get("y")) == (snap.get("x"), snap.get("y")))]
        if not mine:
            out(f"  costume {t.get('costume')}: not placed, so never shown on start")
        for r in mine:
            out("  " + ("shown   " if r["shown"] else "hidden  ") + start_row_text(r))
    out("  (shown: placed on entry, not the player, one costume per actor; mroom start ROOM lists the whole room)")


def cmd_start(args, game: "Game"):
    room = game.find_room(args.room)
    d = game.room_data(room)
    if not d:
        raise SystemExit("mroom: start needs the room data (export with Room data, or mprobe export)")
    rows = start_rows(game, room)
    shown = [r for r in rows if r["shown"]]
    hidden = [r for r in rows if not r["shown"]]
    out(f"Room {room.label} as it opens: {len(shown)} costumes shown, {len(hidden)} hidden")
    out(f"\nShown ({len(shown)}):")
    for r in sorted(shown, key=lambda r: (r["p"].get("y", 0), r["p"].get("actor") or 0)):
        out("  " + start_row_text(r))
    out(f"\nHidden ({len(hidden)}):")
    order = {"the player": 0, "in a cutscene": 1, "later": 2}
    for r in sorted(hidden, key=lambda r: (order.get(r["why"], 3), r["costume"] or 0)):
        out("  " + start_row_text(r))
    unplaced = [c for c in d.get("costumes", []) if not c.get("placed")]
    if unplaced:
        out(f"\nNot placed where MMUCS can read it ({len(unplaced)}): " + ", ".join(
            f"{c.get('costume')}" + (f" (worn by {', '.join(map(str, c['worn_by']))})" if c.get("worn_by") else
                                    f" (dressed by {c['dressed_by']})" if c.get("dressed_by") is not None else "")
            for c in unplaced))
    imgs = [o for o in d.get("objects", []) if o.get("image") and o["image"].get("states")]
    if imgs:
        hid = [o["name"] for o in imgs if o.get("entry_state") == 0]
        out(f"\nObject images: {len(imgs) - len(hid)} shown, {len(hid)} hidden (state 0 on entry)"
            + (f": {', '.join(hid[:args.limit])}" + (" …" if len(hid) > args.limit else "") if hid else ""))
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
    s = sub.add_parser("review")
    s.add_argument("id", nargs="?", type=int)
    s.add_argument("--changed", action="store_true", help="only marks MMUCS now works out differently")
    s.add_argument("--tag", help="only marks with this tag (position, scale, order, zplane, object, frame, missing)")
    s.add_argument("--done", type=int, metavar="N", help="clear mark N")
    s.add_argument("--marks", help="the review file (default: MMUCS's, else the export's copy)")
    s = sub.add_parser("start")
    s.add_argument("room")
    s.add_argument("--limit", type=int, default=20, help="hidden object names to list")
    s = sub.add_parser("compare")
    s.add_argument("other", help="the export to compare with (made before the change)")
    s.add_argument("room", nargs="?")
    s.add_argument("--all", action="store_true", help="list unchanged rooms too")
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
               "lines": cmd_lines, "actor": cmd_actor, "costume": cmd_costume, "review": cmd_review, "start": cmd_start, "compare": cmd_compare,
               "find": cmd_find}[args.command]
    try:
        return handler(args, game)
    except BrokenPipeError:
        return 0


if __name__ == "__main__":
    sys.exit(main())
