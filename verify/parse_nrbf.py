#!/usr/bin/env python3
"""minimal NRBF reader for the game's display settings .dat files"""

import struct
import sys
from pathlib import Path

PRIM = {
    1: ('bool', 1),
    2: ('byte', 1),
    3: ('char', 2),
    5: ('decimal', 16),
    6: ('double', 8),
    7: ('int16', 2),
    8: ('int32', 4),
    9: ('int64', 8),
    10: ('sbyte', 1),
    11: ('single', 4),
    12: ('timespan', 8),
    13: ('datetime', 8),
    14: ('uint16', 2),
    15: ('uint32', 4),
    16: ('uint64', 8),
}


def parse(path):
    data = Path(path).read_bytes()
    pos = 0

    def u8():
        nonlocal pos
        b = data[pos]
        pos += 1
        return b

    def i32():
        nonlocal pos
        v = struct.unpack_from('<i', data, pos)[0]
        pos += 4
        return v

    def string():
        nonlocal pos
        n = u8()
        if n == 0xFF:
            return None
        s = data[pos:pos + n].decode('utf-8')
        pos += n
        return s

    records = []
    assert u8() == 0, 'no stream header'
    root = (i32(), i32(), i32(), i32())
    while pos < len(data):
        rec = u8()
        if rec == 0x0C:  # binary library
            lib_id = i32()
            name = string()
            records.append(('library', lib_id, name))
        elif rec == 0x05:  # class with members and types
            obj_id = i32()
            name = string()
            count = i32()
            members = [string() for _ in range(count)]
            kinds = [u8() for _ in range(count)]
            extra = []
            for kind in kinds:
                if kind == 0:
                    extra.append(u8())
                elif kind in (3, 4):
                    extra.append(string())
                else:
                    extra.append(None)
            values = {}
            for member, kind, info in zip(members, kinds, extra):
                if kind == 0:
                    pname, width = PRIM[info]
                    raw = data[pos:pos + width]
                    pos += width
                    if pname == 'bool':
                        values[member] = bool(raw[0])
                    else:
                        values[member] = int.from_bytes(raw, 'little', signed=pname
                                                       not in ('byte', 'uint16', 'uint32', 'uint64'))
                elif kind == 1:
                    values[member] = string()
                else:
                    raise ValueError(f'{member}: unsupported member kind {kind}')
            records.append(('object', obj_id, name, values))
        elif rec == 0x0B:
            records.append(('end',))
            break
        elif rec == 0x00:
            raise ValueError(f'extra header at {pos - 1:#x}')
        else:
            raise ValueError(f'unknown record {rec:#x} at {pos - 1:#x}')
    return root, records


if __name__ == '__main__':
    root, records = parse(sys.argv[1])
    print('header', root)
    for rec in records:
        print(rec)
