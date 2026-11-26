"""Turn a copy of a GRID Autosport career into a completed one (reference for the launcher).

Edits only fixed-size fields of the RaceHistory component, in both career files, so no structure
changes size. Measured on Steam saves and checked in the game (26 Nov 2026):

  RaceHistory: tag, version 5, header (1337, 174, 41), two statistics tables (174 + 41 entries of
  16 bytes), four 8-byte fields, scalars (4, 8, 4, 4), three counted word lists, 2 bytes, a counted
  list of 28-byte keyed entries, then 7 packages (count, then size + payload). Package 1 (the base
  game) payload: words (5, 1), the cup table (40 slots: 0, or 1 + a 104-byte record), the championship
  table (50 slots: 0, or 1 + a 76-byte record), then one 152-byte record per racing style.
  Cup and championship records start (id, best position from 0 or -1, times completed, wins), with
  wins with the Ravenwest team at word 16 and the season of the first such win at word 19 of a
  championship slot, and the Ravenwest wins at word 21 of a cup slot (words counted from the flag). The Awards screen shows best position, "TIMES COMPLETED", for a 1st
  place (gold) "WINS", and with a Ravenwest win (platinum) "RAVENWEST WINS" / "WON WITH RAVENWEST IN
  SEASON"; a discipline's Ravenwest award counts its competitions won with Ravenwest.
  A racing-style record starts (style, XP); the game derives the level (database career_xp_level:
  level 10 at 1,000,000 XP, 12 at 9,999,999), and five disciplines at level 10 unlock the GRID Grand
  Slam, Masters Trophy and Legends Series championships. Words 32-34 are the seasons in which levels
  10, 11 and 12 were reached (the level milestone awards' "COMPLETED IN SEASON").

complete_career.py SOURCE_REMOTE DEST_REMOTE: write the completed career's three files to DEST_REMOTE.
"""
import struct
import sys
from pathlib import Path

import stock_save

HEADER = struct.pack("<III", 1337, 174, 41)
DISCIPLINES = (2, 3, 4, 5, 6)       # touring, endurance, open wheel, tuner, street
MAX_XP = 9_999_999                  # career_xp_level 12
CUP_SLOTS, CUP_RECORD = 40, 104
CHAMPIONSHIP_SLOTS, CHAMPIONSHIP_RECORD = 50, 76
STYLE_RECORD = 148


def tag(name):
    value = 5381
    for byte in name.lower().encode("ascii"):
        value = (33 * value + byte) & 0xFFFFFFFF
    return struct.pack("<I", value)


class Words:
    def __init__(self, data, pos):
        self.data, self.pos = data, pos

    def word(self):
        if self.pos + 4 > len(self.data):
            raise ValueError("Truncated RaceHistory")
        value = struct.unpack_from("<i", self.data, self.pos)[0]
        self.pos += 4
        return value

    def skip(self, size):
        if size < 0 or self.pos + size > len(self.data):
            raise ValueError("Truncated RaceHistory")
        self.pos += size


def base_package(history):
    """Offset of package 1's payload within a RaceHistory component (starting at its tag)."""
    if history[4] != 5 or history[5:17] != HEADER:
        raise ValueError("Unsupported RaceHistory version")
    cursor = Words(history, 17)
    cursor.skip((174 + 41) * 16 + 4 * 8 + 4 + 8 + 4 + 4)
    for _ in range(3):
        count = cursor.word()
        if not 0 <= count < 4096:
            raise ValueError("Unexpected RaceHistory list")
        cursor.skip(4 * count)
    cursor.skip(2)
    keyed = cursor.word()
    if not 0 <= keyed < 4096:
        raise ValueError("Unexpected RaceHistory entries")
    cursor.skip(28 * keyed)
    if cursor.word() != 7:
        raise ValueError("Unexpected RaceHistory packages")
    size = cursor.word()
    start = cursor.pos
    if size <= 0 or start + size > len(history) or cursor.word() != 5 or cursor.word() != 1:
        raise ValueError("Unexpected base-game RaceHistory package")
    return cursor.pos, start + size


def complete_history(history, cups, championships):
    data = bytearray(history)
    pos, end = base_package(data)

    def table(slots, record, known, raven_word, season_word):
        nonlocal pos
        seen = []
        for _ in range(slots):
            flag = struct.unpack_from("<i", data, pos)[0]
            if flag == 0:
                pos += 4
                continue
            entry, best, completed, wins = struct.unpack_from("<4i", data, pos + 4)
            if flag != 1 or entry not in known or entry in seen or best < -1 or completed < 0 or wins < 0:
                raise ValueError("Unexpected career award record")
            struct.pack_into("<3i", data, pos + 8, 0, max(completed, 1), max(wins, 1))
            raven = struct.unpack_from("<i", data, pos + 4 * raven_word)[0]
            if raven < 0:
                raise ValueError("Unexpected career award record")
            struct.pack_into("<i", data, pos + 4 * raven_word, max(raven, 1))
            if season_word:
                season = struct.unpack_from("<i", data, pos + 4 * season_word)[0]
                if season < 0:
                    raise ValueError("Unexpected career award record")
                struct.pack_into("<i", data, pos + 4 * season_word, season or 1)
            seen.append(entry)
            pos += 4 + record
            if pos > end:
                raise ValueError("Truncated career award table")
        if set(seen) != known:
            raise ValueError("The career is missing cups or championships. Start Career in GRID Autosport and save first.")

    table(CUP_SLOTS, CUP_RECORD, cups, 21, None)
    table(CHAMPIONSHIP_SLOTS, CHAMPIONSHIP_RECORD, championships, 16, 19)
    found = set()
    while pos + 12 <= end and struct.unpack_from("<i", data, pos)[0] == 1:
        style, xp = struct.unpack_from("<2i", data, pos + 4)
        if style in found or not 0 <= xp <= MAX_XP:
            raise ValueError("Unexpected racing style record")
        found.add(style)
        if style in DISCIPLINES:
            struct.pack_into("<i", data, pos + 8, MAX_XP)
            for level in range(3):
                at = pos + 128 + 4 * level
                season = struct.unpack_from("<i", data, at)[0]
                if season < 0:
                    raise ValueError("Unexpected racing style record")
                struct.pack_into("<i", data, at, season or 1)
        pos += 4 + STYLE_RECORD
    if not set(DISCIPLINES) <= found:
        raise ValueError("The career is missing a discipline")
    return bytes(data)


def complete(group, cups, championships):
    start = group.find(tag("RaceHistory"))
    end = group.find(tag("career manager sdu name"))
    if start < 0 or end <= start or group.count(tag("RaceHistory")) != 1:
        raise ValueError("Unrecognized GRID Autosport career layout")
    return group[:start] + complete_history(group[start:end], cups, championships) + group[end:]


# Base-game cups and championships (GRID Autosport database, game.dat database/database.bin).
CUPS = frozenset({1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 28,
                  29, 30, 31, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44})
CHAMPIONSHIPS = frozenset(set(range(41, 86)) | {94, 95, 96})


def main(source, dest, executable):
    reader, writer = stock_save.StockReader(executable), stock_save.StockReader(executable)
    label, _, groups = stock_save.read_career(reader, source)
    files = stock_save.write_career(writer, reader, label, [complete(g, CUPS, CHAMPIONSHIPS) for g in groups])
    dest = Path(dest)
    for name, data in files.items():
        (dest / name).write_bytes(data)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
