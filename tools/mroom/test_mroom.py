"""Tests for mroom, against a small export written to a temporary folder.

Run from this folder:  python -m unittest test_mroom
"""
import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

import mroom


def header(room, title, extra=""):
    return f"// Room {room} - {title}\n// LSCR @0x00000000 in COMI.LA1, 10 bytes{extra}\n\n"


SCRIPTS = {
    "global/script_0006.scumm": "// Global\n// SCRP\n\nvar261 = var261 + 1\nend-script\n",
    # Started only from the town door: its room change counts as the town's exit.
    "global/script_0158.scumm": "// Global\n// SCRP\n\ncurrent-room 16\nend-script\n",
    # Started from two rooms: not an exit of either.
    "global/script_0200.scumm": "// Global\n// SCRP\n\ncurrent-room 9\nend-script\n",
    "rooms/009_cannon/entry.scumm": header("9 cannon", "Room entry") +
        "put-actor 1 at 320,480 in-room 9\nactor 1 costume 2\nstart-script 200\nend-script\n",
    "rooms/009_cannon/objects/0278_cannon-door.scumm": header("9 cannon", "cannon-door", ", object #278, verbs 6, 9") +
        "object cannon-door {\n    verb 6 {\n        say-line 1 \"/CNGT001/A sturdy door.\"\n        end-script\n    }\n"
        "    verb 9 {\n        come-out-door town-cannon-door at 10,20\n        end-script\n    }\n}\n",
    "rooms/015_town/local_2001.scumm": header("15 town", "Local script 2001") +
        "actor 4 costume 75\nput-actor 4 at 552,268 in-room current-room\nsay-line 4 \"/TWKN001/We're closed.\"\n"
        "if (var4999 == 0) {\n    var4999 = 1\n}\nstart-sound 203\nstart-script 200\nend-script\n",
    "rooms/015_town/objects/0397_town-chicken-door.scumm": header("15 town", "town-chicken-door", ", object #397, verbs 6, 9") +
        "object town-chicken-door {\n    verb 6 {\n        say-line 1 \"/TWGT398/They're closed.\"\n        end-script\n    }\n"
        "    verb 9 {\n        start-script 158\n        end-script\n    }\n}\n",
    "rooms/015_town/objects/0398_town-cannon-door.scumm": header("15 town", "town-cannon-door", ", object #398, verbs 9") +
        "object town-cannon-door {\n    verb 9 {\n        come-out-door cannon-door at 1,2\n        end-script\n    }\n}\n",
    # cannon-v's cannon: placed with a sum, lifted by an elevation.
    "rooms/009_cannon/local_2019.scumm": header("9 cannon", "Local script 2019") +
        "actor 3 default\nactor 3 costume 35\nactor 3 elevation 300\nput-actor 3 at 320,492 + 300 in-room 9\n"
        "actor 3 scale 128\nput-actor 3 at x-of(3),y-of(3) in-room current-room\nend-script\n",
    "rooms/016_chkn-int/local_2000.scumm": header("16 chkn-int", "Local script 2000") +
        "verb dialog-choice-verb name \"/CIGT001/I'd like some chicken.\"\nsay-line 1 \"/CIGT002/Hello?\"\n"
        "if (town-chicken-door == 0) {\n}\nend-script\n",
}

INDEX = {"Globals": {"266": {"Name": "item-icon", "Source": "map"}}, "Bits": {}}

TOWN = {
    "format": 1, "room": 15, "name": "town", "folder": "015_town", "file": "COMI.LA1",
    "width": 2096, "height": 480, "palettes": 1, "sounds": 14,
    "objects": [
        {"id": 397, "name": "town-chicken-door", "title": "door", "verbs": [6, 9], "state": 0, "classes": [6],
         "image": {"x": 1288, "y": 168, "width": 48, "height": 144, "states": 1, "actor_dir": 315, "walk_to": [[1332, 321]]}},
        {"id": 398, "name": "town-cannon-door", "verbs": [9], "state": 0, "classes": [32]},
        {"id": None, "name": "town-scenery", "image": {"x": 0, "y": 0, "width": 8, "height": 8, "states": 1}},
    ],
    "boxes": [{"index": 1, "corners": [[0, 0], [1, 0], [1, 1], [0, 1]], "mask": 0, "flags": 0, "scale": 255, "scale_slot": 0}],
    "costumes": [
        {"costume": 75, "akos": "0x0192B7D8", "object": "kenny-object", "placed": [{"actor": 4, "x": 552, "y": 268}]},
        {"costume": 79, "akos": "0x01AD8505", "placed": [{"actor": 1, "x": 0, "y": 0, "player": True}]},
        {"costume": 35, "akos": "0x01125269", "placed": [{"actor": 3, "x": 320, "y": 792, "elevation": 300}]},
        {"costume": 36, "akos": "0x01184801", "worn_by": [4], "placed": []},
        {"costume": 67, "akos": "0x0172562C", "placed": [{"dressed_by": 88, "x": 36, "y": 155, "scale": 150}]},
    ],
    "global_scripts_stored": [],
}

GAME = {"format": 1, "objects": [
    {"id": 278, "name": "cannon-door", "room": 9, "state": 0, "classes": []},
    {"id": 397, "name": "town-chicken-door", "room": 15, "state": 0, "classes": [6]},
    {"id": 398, "name": "town-cannon-door", "room": 15, "state": 0, "classes": [32]},
    {"id": 500, "name": "kenny-object", "room": 15, "state": 0, "classes": []},
]}


def run(*argv):
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        code = mroom.main(list(argv))
    return code, buf.getvalue()


class MroomTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        scripts = self.root / "scripts"
        for rel, text in SCRIPTS.items():
            p = scripts / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(text, encoding="utf-8")
        (scripts / "variables.index.json").write_text(json.dumps(INDEX), encoding="utf-8")
        self.with_data()

    def with_data(self):
        data = self.root / "data"
        (data / "rooms").mkdir(parents=True, exist_ok=True)
        (data / "rooms" / "015_town.json").write_text(json.dumps(TOWN), encoding="utf-8")
        (data / "game.json").write_text(json.dumps(GAME), encoding="utf-8")

    def tearDown(self):
        self.tmp.cleanup()

    def mroom(self, *argv):
        code, text = run("--root", str(self.root), *argv)
        self.assertEqual(code, 0, text)
        return text

    def test_rooms_lists_exits(self):
        text = self.mroom("rooms")
        self.assertRegex(text, r"9 cannon .* 15\n")
        self.assertRegex(text, r"15 town .* 9, 16\n")
        self.assertIn("3 rooms", text)

    def test_room_exits_through_doors_and_owned_global_scripts(self):
        text = self.mroom("room", "town")
        self.assertIn("2096x480", text)
        self.assertRegex(text, r"→ 9 cannon\s+come-out-door cannon-door  from objects/0398_town-cannon-door.scumm:6 \(verb walk-to\)")
        self.assertRegex(text, r"→ 16 chkn-int\s+current-room 16 .* via global/script_0158.scumm:4")
        self.assertRegex(text, r"← 9 cannon\s+come-out-door town-cannon-door  from rooms/009_cannon/")
        self.assertIn("(verb walk-to)  via global/script_0158.scumm:4", text)
        # Script 200 is started from two rooms, so it's nobody's exit.
        self.assertNotIn("script_0200", text)

    def test_room_objects_actors_dialogue_variables(self):
        text = self.mroom("room", "15")
        self.assertRegex(text, r'397 town-chicken-door\s+"door"\s+1288,168 48x144\s+look walk-to')
        self.assertIn("[untouchable]", text)
        self.assertIn("town-scenery", text)
        self.assertRegex(text, r"4 \(kenny-object\)\s+costumes 36, 75;  1 lines;  put at 552,268")
        self.assertIn("Dialogue: 2 lines", text)
        self.assertIn("Variables: 1 used here: 1 unnamed", text)
        self.assertIn("var4999 (2)", text)
        self.assertIn("Sounds started: 203", text)

    def test_actor_names_stay_in_their_room(self):
        # Actor 4 is kenny in town only; the player holds everywhere.
        text = self.mroom("lines", "--limit", "50")
        self.assertIn("rooms/015_town/local_2001.scumm:6  4 (kenny-object): We're closed.", text)
        self.assertIn("rooms/016_chkn-int/local_2000.scumm:5  1 (player): Hello?", text)
        self.assertIn("choice: I'd like some chicken.", text)

    def test_object(self):
        text = self.mroom("object", "397")
        self.assertIn('Object 397 town-chicken-door   hover name "door"', text)
        self.assertIn("room: 15 town", text)
        self.assertIn("walk-to 1332,321", text)
        self.assertIn("verbs: look:5  walk-to:9", text)
        self.assertIn("rooms/016_chkn-int/local_2000.scumm:6", text)

    def test_object_by_name_and_arrivals(self):
        text = self.mroom("object", "cannon-door")
        self.assertIn("Object 278 cannon-door", text)
        self.assertIn("leads to: 15 town (come-out-door town-cannon-door", text)
        self.assertIn("arrive here from: rooms/015_town/objects/0398_town-cannon-door.scumm:6", text)
        many = self.mroom("object", "door")
        self.assertIn("3 objects match 'door'", many)

    def test_find(self):
        text = self.mroom("find", "closed")
        self.assertIn("Lines (2)", text)
        text = self.mroom("find", "town")
        self.assertIn("Rooms: 15 town", text)

    def test_actor(self):
        text = self.mroom("actor", "4")
        self.assertRegex(text, r"15 town\s+1 lines;  costumes 75;  put 1x")

    def test_placements_with_sums_and_elevation(self):
        text = self.mroom("room", "9")
        self.assertIn("put at 320,792 elevation 300 (drawn at 320,492) (local_2019.scumm:7)", text)
        self.assertNotIn("put-actor 3 at x-of", text)

    def test_costume(self):
        text = self.mroom("costume", "35")
        self.assertIn("Costume 35: stored in room 15 town (AKOS 0x01125269)", text)
        self.assertIn("MMUCS places it: actor 3 at 320,792 elevation 300 (drawn at 320,492)", text)
        self.assertIn("rooms/009_cannon/local_2019.scumm:5  actor 3 costume 35", text)
        self.assertIn(":6  elevation 300", text)
        self.assertIn(":7  put-actor at 320,792 elevation 300 (drawn at 320,492) in-room 9", text)
        self.assertIn(":8  scale 128", text)
        self.assertIn(":9  put-actor at x-of(3),y-of(3) elevation 300 (drawn at x-of(3),y-of(3) - 300), scale 128 in-room current-room", text)

    def test_costume_worn_but_placed_at_runtime(self):
        text = self.mroom("costume", "36")
        self.assertIn("worn by actor 4, placed at runtime", text)
        self.assertRegex(self.mroom("room", "15"), r"4 \(kenny-object\)\s+costumes 36, 75")

    def test_costume_on_an_actor_kept_in_a_variable(self):
        text = self.mroom("costume", "67")
        self.assertIn("MMUCS places it: an actor kept in a variable at 36,155, scale 150, dressed by script 88", text)
        self.assertIn("Actors (", self.mroom("room", "15"))  # no actor number doesn't break the room page

    def test_evaluate_and_split(self):
        self.assertEqual(mroom.evaluate("492 + 300"), "792")
        self.assertEqual(mroom.evaluate("100 / 3"), "33")
        self.assertEqual(mroom.evaluate("local3 + 1"), "local3 + 1")
        self.assertEqual(mroom.split_args("x-of(10),y-of(10) + 450"), ["x-of(10)", "y-of(10) + 450"])
        self.assertEqual(mroom.split_args("a[0][b],f(1, 2)"), ["a[0][b]", "f(1, 2)"])

    def test_without_room_data(self):
        import shutil
        shutil.rmtree(self.root / "data")
        text = self.mroom("room", "15")
        self.assertIn("no room data", text)
        self.assertIn("town-chicken-door", text)
        self.assertNotIn("2096x480", text)

    def test_index_is_saved_and_rebuilt(self):
        self.mroom("rooms")
        index = self.root / "scripts" / mroom.INDEX_FILE
        self.assertTrue(index.exists())
        p = self.root / "scripts" / "rooms/015_town/local_2001.scumm"
        p.write_text(p.read_text(encoding="utf-8") + "say-line 1 \"/X/Added.\"\n", encoding="utf-8")
        self.assertIn("Added.", self.mroom("lines", "15"))


if __name__ == "__main__":
    unittest.main()
