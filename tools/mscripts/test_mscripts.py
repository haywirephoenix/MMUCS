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
        "item-icon = 122\ncursor image item-icon frame 1\nload-object item-icon\n"
        "if (class-of(item-icon) is [146]) {\n    var290 = item-icon\n}\n",
    # Exported with offsets: the gutter is removed before matching.
    "rooms/011_waterln/entry.scumm": HEADER.format(title="Room entry") +
        "0000  item-icon = 117\n      }\n0010  end-script\n",
}

INDEX = {
    "Globals": {
        "15": {"Name": "have-msg", "Source": "engine"},
        "266": {"Name": "item-icon", "Source": "map"},
    },
    "Bits": {},
}

MAP = {
    "Globals": {
        "261": {"Name": "", "Note": "busy counter?"},
        "266": {"Name": "item-icon", "Note": "cursor"},
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
        _, by_name = self.cli("var", "item-icon", "--map", str(self.map), "--examples", "0")
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
        self.assertIn("item-icon", text)
        self.assertIn("engine", text)  # have-msg
        self.assertIn("tracked", text)  # var261
        _, unnamed = self.cli("vars", "--unnamed", "--map", str(self.map))
        self.assertNotIn("item-icon", unnamed)

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

    def test_script_snippets(self):
        _, text = self.cli("script", "9", "local_2006", "--lines", "9-11")
        self.assertIn("local_2006.scumm:9-11 of 4-12", text)
        self.assertIn("var498 = var498 + 1", text)
        self.assertNotIn("say-line", text)
        _, text = self.cli("script", "9", "local_2006", "--at", "10", "-C", "1")
        self.assertEqual([l.split()[0] for l in text.splitlines()[1:]], ["9", "10", "11"])
        _, text = self.cli("script", "9", "local_2006", "--block", "5")
        self.assertIn("4-6", text)  # the if, up to its } else if {
        _, text = self.cli("script", "9", "local_2006", "--block", "10")
        self.assertIn("9-11", text)
        _, text = self.cli("script", "9", "local_2006", "--block", "10", "--levels", "2")
        self.assertIn("the whole script", text)
        with self.assertRaises(SystemExit) as e:
            self.cli("script", "9", "local_2006", "--lines", "50-60")
        self.assertIn("no lines 50-60", str(e.exception))

    def test_refs_lists_lines_and_writes(self):
        _, text = self.cli("refs", "498", "--map", str(self.map))
        self.assertIn("rooms/009_cannon/local_2006.scumm:10: var498 = var498 + 1", text)
        self.assertIn("rooms/009_cannon/local_2004.scumm:5:", text)
        self.assertIn("5 lines in 2 scripts, 1 writing it", text)  # line 10 uses it twice, but counts once
        _, text = self.cli("refs", "498", "--writes", "-C", "1", "--map", str(self.map))
        self.assertIn("local_2006.scumm-9- if (var498 < 10) {", text)
        self.assertNotIn("local_2004", text)
        _, text = self.cli("refs", "item-icon", "--by-room", "--map", str(self.map))
        self.assertIn("     5      1  009_cannon", text)
        self.assertIn("     1      1  011_waterln", text)
        _, text = self.cli("refs", "266", "--in", "11", "--map", str(self.map))
        self.assertNotIn("009_cannon", text)

    def test_index_is_saved_and_rebuilt_when_the_export_changes(self):
        self.cli("refs", "498", "--map", str(self.map))
        saved = self.root / "scripts" / mscripts.INDEX_FILE
        self.assertTrue(saved.exists())
        p = self.root / "scripts" / "global" / "script_0006.scumm"
        p.write_text(p.read_text(encoding="utf-8") + "var498 = 3\n", encoding="utf-8")
        _, text = self.cli("refs", "498", "--writes", "--map", str(self.map))
        self.assertIn("global/script_0006.scumm:6: var498 = 3", text)

    def test_progress_counts_and_groups(self):
        _, text = self.cli("progress", "--no-git", "--map", str(self.map))
        self.assertIn("5 globals used in scripts", text)
        for row in ("engine           1", "named            1", "noted            1", "untouched        2"):
            self.assertIn(row, text)
        self.assertIn("    2     0     0     2  009_cannon", text)
        _, text = self.cli("progress", "--no-git", "--left", "9", "--map", str(self.map))
        self.assertIn("var290", text)
        self.assertIn("var498", text)
        self.assertNotIn("var261 ", text)

    def test_name_checks_and_writes_the_map(self):
        def name(*a):
            try:
                return self.cli("name", *a, "--map", str(self.map))
            except SystemExit as e:
                return 1, str(e)
        self.assertIn("kebab-case", name("261", "Busy Count", "--note", "x")[1])
        self.assertIn("numbered variable", name("261", "var12", "--note", "x")[1])
        self.assertIn("engine variable", name("15", "message", "--note", "x")[1])
        self.assertIn("already", name("261", "item-icon", "--note", "x")[1])
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


class CitationTests(unittest.TestCase):
    def parse(self, note):
        return [(rel, spans) for _, rel, spans, _ in mscripts.citations(note)]

    def test_forms(self):
        self.assertEqual(self.parse("a (global/script_0034.scumm:5/58, rooms/009_cannon/local_2006.scumm:18-20)"), [
            ("global/script_0034.scumm", [(5, 5), (58, 58)]),
            ("rooms/009_cannon/local_2006.scumm", [(18, 20)]),
        ])

    def test_bare_line_means_the_script_before(self):
        self.assertEqual(self.parse("global/script_0142.scumm:109-110 sets it, and later (:112)"),
                         [("global/script_0142.scumm", [(109, 110)]), ("global/script_0142.scumm", [(112, 112)])])

    def test_a_script_named_without_a_line_counts(self):
        note = "Written by global/script_0040.scumm (sentence-script = 40, global/script_0001.scumm:193): at :118"
        self.assertEqual(self.parse(note)[-1], ("global/script_0040.scumm", [(118, 118)]))

    def test_bare_line_in_parentheses_continues_the_latest(self):
        note = "global/script_0014.scumm:4-9 shows it. Skipped (global/script_0142.scumm:93-94), and grows (:124-125)."
        self.assertEqual(self.parse(note)[-1], ("global/script_0142.scumm", [(124, 125)]))

    def test_times_and_ratios_are_not_citations(self):
        self.assertEqual(self.parse("at 10:30, a 4:3 picture"), [])


class ReviewTests(unittest.TestCase):
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

    def tearDown(self):
        self.tmp.cleanup()

    def review(self, doc, *extra):
        self.map.write_text(json.dumps(doc), encoding="utf-8")
        try:
            return run("--root", str(self.root), "review", "--all", "--map", str(self.map), *extra)
        except SystemExit as e:
            return 2, str(e)

    def test_citation_statuses(self):
        doc = {"Globals": {"498": {"Name": "wally-line", "Note":
            "Counts up (rooms/009_cannon/local_2006.scumm:10), checked at :4 "
            "(rooms/009_cannon/local_2004.scumm:4), not in global/script_0999.scumm:1 "
            "or rooms/009_cannon/local_2006.scumm:99."}}, "Bits": {}}
        code, text = self.review(doc)
        self.assertEqual(code, 1)  # a wrong citation fails
        self.assertIn("2 ok, 1 to check, 2 wrong", text)
        self.assertIn("no such script", text)
        self.assertIn("has no line 99", text)
        self.assertIn("check rooms/009_cannon/local_2004.scumm:4", text)

    def test_evidence_and_name_checks(self):
        doc = {"Globals": {"261": {"Name": "item-icon", "Note": "global/script_0006.scumm:4"}}, "Bits": {}}
        code, text = self.review(doc)
        self.assertIn("! name:", text)  # item-icon is var266 in the export
        self.assertIn("written in: global/script_0006.scumm, global/script_0007.scumm", text)

    def test_against_a_git_ref(self):
        import shutil
        import subprocess
        if not shutil.which("git"):
            self.skipTest("git isn't installed")
        repo = self.root / "repo"
        repo.mkdir()
        git = lambda *a: subprocess.run(["git", "-C", str(repo), *a], check=True, capture_output=True)
        git("init", "-q")
        git("config", "user.email", "test@example.com")
        git("config", "user.name", "test")
        m = repo / "variables.json"
        m.write_text(json.dumps({"Globals": {"261": {"Name": "", "Note": "busy?"}}, "Bits": {}}), encoding="utf-8")
        git("add", ".")
        git("commit", "-q", "-m", "base")
        m.write_text(json.dumps({"Globals": {"261": {"Name": "cutscene-depth", "Note": "global/script_0006.scumm:4"},
                                             "498": {"Name": "", "Note": "unchanged later"}}, "Bits": {}}), encoding="utf-8")
        code, text = run("--root", str(self.root), "review", "--map", str(m))
        self.assertEqual(code, 0, text)
        self.assertIn("var261  cutscene-depth  (new)", text)
        self.assertIn("var498  unnamed", text)
        self.assertIn("2 entries", text)


if __name__ == "__main__":
    unittest.main()
