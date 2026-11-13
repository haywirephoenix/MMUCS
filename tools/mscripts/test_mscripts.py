"""Tests for mscripts, against a small export written to a temporary folder.

Run from this folder:  python -m unittest test_mscripts
"""
import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

import mscripts

HEADER = "// Room 9 cannon - {title}\n// LSCR @0x00000000 in COMI.LA1, 10 bytes\n\n"

FILES = {
    "global/script_0006.scumm": "// Global\n// SCRP\n\nvar261 = var261 + 1\nend-script\n",
    "global/script_0007.scumm": "// Global\n// SCRP\n\nvar261 = var261 - 1\nend-script\n",
    "global/script_0111.scumm": "// Global\n// SCRP\n\nwhile (!var261) {\n    break-here\n}\nif (have-msg) {\n    wait-for-message\n}\n",
    "rooms/009_cannon/local_2006.scumm": HEADER.format(title="Local script 2006") +
        "if (var498 == 0) {\n    say-line 2 \"/A/\"\n} else if (var498 == 1) {\n    say-line 2 \"/B/\"\n}\n"
        "if (var498 < 10) {\n    var498 = var498 + 1\n}\nstart-script bak 2005\n",
    "rooms/009_cannon/local_2004.scumm": HEADER.format(title="Local script 2004") +
        "start-script bak rec 2005(1, 2)\nlocal0 = call-script 77(var498)\n",
    "rooms/009_cannon/objects/0279_ramrod-object.scumm": HEADER.format(title="ramrod") +
        "cursor-object = 122\ncursor image cursor-object frame 1\nload-object cursor-object\n"
        "if (class-of(cursor-object) is [146]) {\n    var290 = cursor-object\n}\n",
    # Exported with offsets: the gutter is removed before matching.
    "rooms/011_waterln/entry.scumm": HEADER.format(title="Room entry") +
        "0000  cursor-object = 117\n      }\n0010  end-script\n",
}

INDEX = {
    "Globals": {
        "15": {"Name": "have-msg", "Source": "engine"},
        "266": {"Name": "cursor-object", "Source": "map"},
    },
    "Bits": {},
}

MAP = {
    "Globals": {
        "261": {"Name": "", "Note": "busy counter?"},
        "266": {"Name": "cursor-object", "Note": "cursor"},
    },
    "Bits": {},
}


def run(*argv):
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        code = mscripts.main(list(argv))
    return code, buf.getvalue()


class MscriptsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        scripts = self.root / "scripts"
        for rel, text in FILES.items():
            p = scripts / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(text, encoding="utf-8")
        (scripts / "variables.index.json").write_text(json.dumps(INDEX), encoding="utf-8")
        self.map = self.root / "variables.json"
        self.map.write_text(json.dumps(MAP), encoding="utf-8")

    def tearDown(self):
        self.tmp.cleanup()

    def cli(self, *argv):
        return run("--root", str(self.root), *argv)

    def test_counter_shape(self):
        _, text = self.cli("var", "498", "--map", str(self.map))
        self.assertIn("+ 1 (increment)", text)
        self.assertIn("== 0", text)
        self.assertIn("< 10", text)
        self.assertIn("written only in: rooms/009_cannon/local_2006.scumm", text)

    def test_increment_and_decrement_in_different_scripts(self):
        _, text = self.cli("var", "var261", "--map", str(self.map))
        self.assertIn("(tracked)", text)
        self.assertIn("busy counter?", text)
        self.assertIn("+ 1 (increment)", text)
        self.assertIn("- 1 (decrement)", text)
        self.assertIn("Tested as true/false: 1x", text)

    def test_names_and_numbers_find_the_same_variable(self):
        _, by_name = self.cli("var", "cursor-object", "--map", str(self.map), "--examples", "0")
        _, by_number = self.cli("var", "266", "--map", str(self.map), "--examples", "0")
        self.assertEqual(by_name, by_number)
        self.assertIn("cursor image", by_name)
        self.assertIn("load-object", by_name)
        self.assertIn("class-of", by_name)
        self.assertIn("var290", by_name)  # copied into
        self.assertIn("= 117", by_name)   # from the file exported with offsets
        self.assertIn("= 122", by_name)

    def test_vars_ranks_and_shows_status(self):
        _, text = self.cli("vars", "--map", str(self.map))
        self.assertIn("cursor-object", text)
        self.assertIn("engine", text)  # have-msg
        self.assertIn("tracked", text)  # var261
        _, unnamed = self.cli("vars", "--unnamed", "--map", str(self.map))
        self.assertNotIn("cursor-object", unnamed)

    def test_calls_match_flags_and_arguments(self):
        _, text = self.cli("calls", "2005")
        self.assertIn("local_2006.scumm", text)
        self.assertIn("local_2004.scumm", text)
        self.assertIn("2 calls", text)
        _, text = self.cli("calls", "77")
        self.assertIn("call-script 77", text)

    def test_search_and_script(self):
        _, text = self.cli("search", "say-line", "--in", "9", "--count")
        self.assertIn("2  rooms/009_cannon/local_2006.scumm", text)
        _, text = self.cli("script", "9", "local_2006")
        self.assertIn("if (var498 == 0) {", text)
        _, text = self.cli("script", "global", "6")
        self.assertIn("var261 = var261 + 1", text)

    def test_name_checks_and_writes_the_map(self):
        def name(*a):
            try:
                return self.cli("name", *a, "--map", str(self.map))
            except SystemExit as e:
                return 1, str(e)
        self.assertIn("kebab-case", name("261", "Busy Count", "--note", "x")[1])
        self.assertIn("numbered variable", name("261", "var12", "--note", "x")[1])
        self.assertIn("engine variable", name("15", "message", "--note", "x")[1])
        self.assertIn("already", name("261", "cursor-object", "--note", "x")[1])
        self.assertIn("keyword", name("261", "while", "--note", "x")[1])
        # The tracked entry's note is kept when no new one is given.
        code, text = name("261", "busy-count")
        self.assertEqual(code, 0, text)
        doc = json.loads(self.map.read_text(encoding="utf-8"))
        self.assertEqual(doc["Globals"]["261"], {"Name": "busy-count", "Note": "busy counter?"})
        # The new name resolves before any re-export.
        _, text = self.cli("var", "busy-count", "--map", str(self.map), "--examples", "0")
        self.assertIn("var261  busy-count", text)

    def test_naming_needs_a_note(self):
        with self.assertRaises(SystemExit) as e:
            self.cli("name", "498", "wally-line", "--map", str(self.map))
        self.assertIn("--note", str(e.exception))

    def test_note_tracks_without_naming(self):
        self.cli("note", "498", "Wally's cannon lines, 0-10", "--map", str(self.map))
        doc = json.loads(self.map.read_text(encoding="utf-8"))
        self.assertEqual(doc["Globals"]["498"], {"Name": "", "Note": "Wally's cannon lines, 0-10"})
        self.assertEqual(list(doc["Globals"]), ["261", "266", "498"])  # kept in number order


if __name__ == "__main__":
    unittest.main()
