#!/usr/bin/env python3
"""Port a GDScript Chiptracker Song .tres resource to the C#/chiptracker_net
equivalent, so it can be loaded via the chiptracker_editor_load_song MCP
tool instead of thousands of individual set_note calls.

Godot's .tres text format is line-based (Godot's own resource saver never
wraps a field's value across lines, even for very long arrays), so this
parses it as a simple recursive-descent value parser over each field line
rather than a general GDScript parser.

Usage:
    python3 port_gd_song.py <source_gd_song.tres> <dest_cs_song.tres>

The source must be a GDScript addons/chiptracker Song resource (Channel/
ChannelGroup/Instrument/Pattern/Cell sub-resources, snake_case fields).
The destination is written referencing addons/chiptracker_net/engine's C#
scripts by uid (PascalCase fields) -- update ENGINE_UIDS below if those
scripts are ever moved/regenerated with new uids.
"""
import re
import sys

ENGINE_UIDS = {
    "channel": ("1_channel", "uid://fkhoak7ffhxw", "Channel.cs"),
    "channel_group": ("2_group", "uid://sbm6ie3s0lo8", "ChannelGroup.cs"),
    "instrument": ("3_instrument", "uid://b56h1bhrtuhfe", "Instrument.cs"),
    "pattern": ("4_pattern", "uid://c4v0u6v8krpta", "Pattern.cs"),
    "cell": ("5_cell", "uid://cuiqatlhpwft6", "Cell.cs"),
    "song": ("6_song", "uid://dr6d64cvmmtg0", "Song.cs"),
}

FIELD_RENAME = {
    "instrument_id": "InstrumentId", "instrument_type": "InstrumentType",
    "group_index": "GroupIndex", "duty_cycle": "DutyCycle",
    "envelope_times": "EnvelopeTimes", "pitch_envelope": "PitchEnvelope",
    "pitch_envelope_times": "PitchEnvelopeTimes", "pitch_range": "PitchRange",
    "step_offset": "StepOffset", "fixed_note_enabled": "FixedNoteEnabled",
    "fixed_note": "FixedNote", "rows_per_beat": "RowsPerBeat",
    "rows_per_pattern": "RowsPerPattern", "order_list": "OrderList",
    "note": "Note", "volume": "Volume", "effect": "Effect",
    "effect_param": "EffectParam", "name": "Name", "id": "Id",
    "waveform": "Waveform", "envelope": "Envelope", "muted": "Muted",
    "solo": "Solo", "rows": "Rows", "tempo": "Tempo", "channels": "Channels",
    "patterns": "Patterns", "instruments": "Instruments", "groups": "Groups",
    "layers": "Layers", "arpeggio": "Arpeggio",
    "metronome_accent": "MetronomeAccent",
}

ARRAY_TYPE = {
    "Envelope": "float", "EnvelopeTimes": "float", "PitchEnvelope": "float",
    "PitchEnvelopeTimes": "float", "Arpeggio": "int", "OrderList": "int",
    "Channels": 'ExtResource("1_channel")', "Groups": 'ExtResource("2_group")',
    "Instruments": 'ExtResource("3_instrument")',
    "Patterns": 'ExtResource("4_pattern")',
    "Layers": 'ExtResource("3_instrument")', "Rows": "Array",
}


# ---------- parse ----------

def parse_value(s, i):
    while i < len(s) and s[i] in " \t":
        i += 1
    if s[i] == '"':
        j = i + 1
        out = []
        while s[j] != '"':
            if s[j] == '\\':
                out.append(s[j + 1]); j += 2
            else:
                out.append(s[j]); j += 1
        return "".join(out), j + 1
    if s[i] == '[':
        items = []
        j = i + 1
        while True:
            while s[j] in " \t":
                j += 1
            if s[j] == ']':
                j += 1
                break
            val, j = parse_value(s, j)
            items.append(val)
            while s[j] in " \t":
                j += 1
            if s[j] == ',':
                j += 1
        return items, j
    if s[i:i + 6] == "Array[":
        j = i + 6
        depth = 1
        start = j
        while depth > 0:
            if s[j] == '[':
                depth += 1
            elif s[j] == ']':
                depth -= 1
            j += 1
        type_expr = s[start:j - 1]
        assert s[j] == '(', f"expected ( after Array[...] at {j}"
        val, j2 = parse_value(s, j + 1)
        while s[j2] in " \t":
            j2 += 1
        assert s[j2] == ')', f"expected ) at {j2}"
        return ("array", val, type_expr), j2 + 1
    m = re.match(r'[A-Za-z_][A-Za-z0-9_]*', s[i:])
    if m is None:
        m2 = re.match(r'-?\d+\.\d+(?:[eE][+-]?\d+)?', s[i:])
        if m2:
            return float(m2.group(0)), i + len(m2.group(0))
        m3 = re.match(r'-?\d+', s[i:])
        if m3:
            return int(m3.group(0)), i + len(m3.group(0))
        raise ValueError(f"cannot parse value at {i}: {s[i:i + 40]!r}")
    ident = m.group(0)
    j = i + len(ident)
    if j < len(s) and s[j] == '(':
        val, j2 = parse_value(s, j + 1)
        while s[j2] in " \t":
            j2 += 1
        assert s[j2] == ')', f"expected ) at {j2}"
        j2 += 1
        if ident == "SubResource":
            return ("ref", val), j2
        if ident == "ExtResource":
            return ("extref", val), j2
        return (ident, val), j2
    if ident in ("true", "false"):
        return ident == "true", j
    m2 = re.match(r'-?\d+\.\d+(?:[eE][+-]?\d+)?', s[i:])
    if m2:
        return float(m2.group(0)), i + len(m2.group(0))
    m3 = re.match(r'-?\d+', s[i:])
    if m3:
        return int(m3.group(0)), i + len(m3.group(0))
    raise ValueError(f"cannot parse value at {i}: {s[i:i + 40]!r}")


def parse_field_line(line):
    key, _, rest = line.partition(" = ")
    val, _ = parse_value(rest, 0)
    return key, val


def parse_source(path):
    text = open(path, encoding="utf-8").read()
    blocks = []
    cur_header, cur_lines = None, []
    for line in text.split("\n"):
        if line.startswith("["):
            if cur_header is not None:
                blocks.append((cur_header, cur_lines))
            cur_header, cur_lines = line, []
        elif line.strip():
            cur_lines.append(line)
    if cur_header is not None:
        blocks.append((cur_header, cur_lines))

    ext_map, sub_map, song_fields = {}, {}, {}
    id_re = re.compile(r'(?<![a-zA-Z])id="([^"]+)"')
    for header, body in blocks:
        if header.startswith("[ext_resource"):
            m_id, m_path = id_re.search(header), re.search(r'path="([^"]+)"', header)
            if m_id and m_path:
                ext_map[m_id.group(1)] = m_path.group(1)
        elif header.startswith("[sub_resource"):
            sid = id_re.search(header).group(1)
            fields, script_id = {}, None
            for line in body:
                key, val = parse_field_line(line)
                if key == "script":
                    script_id = val[1]
                else:
                    fields[key] = val
            typ = ext_map.get(script_id, "").rsplit("/", 1)[-1].replace(".gd", "")
            sub_map[sid] = {"type": typ, "fields": fields}
        elif header.startswith("[resource]"):
            for line in body:
                key, val = parse_field_line(line)
                song_fields[key] = val
    return sub_map, song_fields


# ---------- generate ----------

def is_tagged(v):
    return isinstance(v, tuple) and len(v) >= 2 and isinstance(v[0], str) and v[0] in ("ref", "extref", "array")


def esc_str(s):
    return s.replace("\\", "\\\\").replace('"', '\\"')


def fmt_scalar(v):
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, int):
        return str(v)
    if isinstance(v, float):
        return repr(v)
    if isinstance(v, str):
        return f'"{esc_str(v)}"'
    raise TypeError(f"unexpected scalar {v!r}")


def fmt_value(key, val):
    if is_tagged(val):
        tag = val[0]
        if tag == "ref":
            return f'SubResource("{val[1]}")'
        if tag == "extref":
            return f'ExtResource("{val[1]}")'
        if tag == "array":
            arr_t = ARRAY_TYPE.get(key, "Array")
            items = ", ".join(fmt_value(key, it) for it in val[1])
            return f'Array[{arr_t}]([{items}])'
    if isinstance(val, list):
        return "[" + ", ".join(fmt_value(key, it) for it in val) + "]"
    return fmt_scalar(val)


def refs_of(val):
    ids = []
    def walk(v):
        if is_tagged(v):
            if v[0] == "ref":
                ids.append(v[1])
            elif v[0] == "array":
                for it in v[1]:
                    walk(it)
        elif isinstance(v, list):
            for it in v:
                walk(it)
    walk(val)
    return ids


def generate(sub_map, song_fields, dest_path):
    out = ['[gd_resource type="Resource" script_class="Song" format=3]', ""]
    for typ, (local_id, uid, filename) in ENGINE_UIDS.items():
        out.append(f'[ext_resource type="Script" uid="{uid}" '
                    f'path="res://addons/chiptracker_net/engine/{filename}" id="{local_id}"]')
    out.append("")

    emitted = set()

    def emit(sid):
        if sid in emitted:
            return
        emitted.add(sid)
        entry = sub_map[sid]
        local_id = ENGINE_UIDS[entry["type"]][0]
        out.append(f'[sub_resource type="Resource" id="{sid}"]')
        out.append(f'script = ExtResource("{local_id}")')
        for k, v in entry["fields"].items():
            new_key = FIELD_RENAME.get(k, k)
            out.append(f"{new_key} = {fmt_value(new_key, v)}")
        out.append("")

    for group_key in ("channels", "groups", "instruments"):
        for sid in refs_of(song_fields[group_key]):
            emit(sid)
            for lid in refs_of(sub_map[sid]["fields"].get("layers", [])):
                emit(lid)
    for pid in refs_of(song_fields["patterns"]):
        rows = sub_map[pid]["fields"].get("rows")
        if rows is not None:
            for cid in refs_of(rows):
                emit(cid)
        emit(pid)

    out.append("[resource]")
    out.append(f'script = ExtResource("{ENGINE_UIDS["song"][0]}")')
    for k in ("tempo", "rows_per_beat", "rows_per_pattern", "channels",
              "order_list", "patterns", "instruments", "groups"):
        new_key = FIELD_RENAME.get(k, k)
        out.append(f"{new_key} = {fmt_value(new_key, song_fields[k])}")

    text = "\n".join(out) + "\n"
    open(dest_path, "w", encoding="utf-8").write(text)
    print(f"wrote {len(text)} bytes, {len(emitted)} sub-resources -> {dest_path}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(1)
    sub_map, song_fields = parse_source(sys.argv[1])
    generate(sub_map, song_fields, sys.argv[2])
