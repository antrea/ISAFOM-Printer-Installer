"""Static analysis of original KX binaries. Does not load or execute a driver.

Dependencies are installed only in the adjacent vendor directory. All reported
addresses are RVAs of the exact input hash, never offsets for a DEVMODE patch.
"""
import argparse
import bisect
import hashlib
import json
from pathlib import Path
import re
import struct
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).parent / 'vendor'))
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86_const import X86_OP_MEM, X86_OP_IMM, X86_REG_RIP


class Image:
    def __init__(self, path):
        self.path = Path(path)
        self.data = self.path.read_bytes()
        self.pe = pefile.PE(data=self.data, fast_load=True)
        self.pe.parse_data_directories(directories=[0, 1, 3])
        if self.pe.FILE_HEADER.Machine != 0x8664:
            raise ValueError('Only x64 images supported')
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.functions = sorted((x.struct.BeginAddress, x.struct.EndAddress)
                                for x in getattr(self.pe, 'DIRECTORY_ENTRY_EXCEPTION', []))
        self.starts = [a for a, b in self.functions]
        self.exports = {x.address: x.name.decode('ascii', 'replace')
                        for x in getattr(getattr(self.pe, 'DIRECTORY_ENTRY_EXPORT', None), 'symbols', []) if x.name}
        self.imports = {x.address - self.base: d.dll.decode('ascii', 'replace') + '!' +
                        (x.name.decode('ascii', 'replace') if x.name else '#' + str(x.ordinal))
                        for d in getattr(self.pe, 'DIRECTORY_ENTRY_IMPORT', []) for x in d.imports}
        self.md = Cs(CS_ARCH_X86, CS_MODE_64)
        self.md.detail = True

    def bounds(self, rva):
        i = bisect.bisect_right(self.starts, rva) - 1
        if i >= 0 and self.functions[i][0] <= rva < self.functions[i][1]:
            return self.functions[i]
        return rva, rva + 256

    def label(self, rva):
        if rva in self.imports:
            return self.imports[rva]
        if rva in self.exports:
            return self.exports[rva]
        try:
            d = self.pe.get_data(rva, 200)
            ascii_match = re.match(rb'[\x20-\x7e]{4,}\x00', d)
            if ascii_match:
                return repr(ascii_match.group()[:-1].decode('ascii'))
            wide = re.match(rb'(?:[\x20-\x7e]\x00){4,}\x00\x00', d)
            if wide:
                return repr(wide.group()[:-2].decode('utf-16-le'))
        except pefile.PEFormatError:
            pass
        return ''

    def strings(self, query):
        found = []
        for enc, pattern in [('ascii', rb'[\x20-\x7e]{4,}'), ('utf-16-le', rb'(?:[\x20-\x7e]\x00){4,}')]:
            for m in re.finditer(pattern, self.data):
                value = m.group().decode(enc)
                if len(value) <= 240 and re.search(query, value, re.I):
                    rva = self.pe.get_rva_from_offset(m.start())
                    found.append({'rva': hex(rva), 'encoding': enc, 'text': value})
        return found

    def refs(self, targets):
        # Bound this first-pass search to common RIP-relative LEA references.
        # Each candidate is decoded. This is not an exhaustive xref analysis:
        # pointer tables, MOV and computed addresses require separate inspection.
        found = []
        pattern = rb'[\x48\x4c]\x8d[\x05\x0d\x15\x1d\x25\x2d\x35\x3d].{4}'
        for section in self.pe.sections:
            if not section.Characteristics & 0x20000000:
                continue
            raw = section.get_data()
            for m in re.finditer(pattern, raw, re.S):
                rva = section.VirtualAddress + m.start()
                dest = rva + 7 + struct.unpack('<i', m.group()[3:])[0]
                if dest not in targets:
                    continue
                ins = next(self.md.disasm(m.group(), self.base + rva), None)
                if ins and ins.size == 7 and ins.mnemonic == 'lea':
                    found.append({'instruction': hex(rva), 'target': hex(dest),
                                  'text': targets[dest], 'function': hex(self.bounds(rva)[0])})
        return found

    def disassemble(self, rva, limit):
        begin, end = self.bounds(rva)
        end = min(end, begin + limit)
        lines = [f'{self.path.name} sha256={hashlib.sha256(self.data).hexdigest()}',
                 f'Function RVA {hex(begin)}..{hex(end)}; ImageBase={hex(self.base)}']
        for ins in self.md.disasm(self.pe.get_data(begin, end - begin), self.base + begin):
            notes = []
            for operand in ins.operands:
                if operand.type == X86_OP_MEM and operand.mem.base == X86_REG_RIP:
                    dest = ins.address + ins.size + operand.mem.disp - self.base
                    notes.append(hex(dest) + ' ' + self.label(dest))
                elif operand.type == X86_OP_IMM and ins.mnemonic.startswith(('call', 'j')):
                    dest = operand.imm - self.base
                    notes.append(hex(dest) + ' ' + self.label(dest))
            lines.append(f'{ins.address-self.base:08x}  {ins.mnemonic:8} {ins.op_str}' +
                         (' ; ' + '; '.join(notes) if notes else ''))
        return '\n'.join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('path')
    parser.add_argument('--refs')
    parser.add_argument('--function', type=lambda x: int(x, 0))
    parser.add_argument('--limit', type=int, default=16000)
    parser.add_argument('--output')
    args = parser.parse_args()
    image = Image(args.path)
    if args.function is not None:
        output = image.disassemble(args.function, args.limit)
    elif args.refs:
        strings = image.strings(args.refs)
        output = json.dumps({'file': image.path.name, 'sha256': hashlib.sha256(image.data).hexdigest(),
                             'strings': strings, 'references': image.refs({int(s['rva'], 0): s['text'] for s in strings}),
                             'coverage': 'Common RIP-relative LEA only; not exhaustive'}, indent=2)
    else:
        output = json.dumps({'exports': image.exports, 'imports': image.imports}, indent=2)
    if args.output:
        dest = Path(args.output).resolve()
        if not dest.is_relative_to(Path(__file__).parent.resolve()):
            raise ValueError('Output must stay inside analysis directory')
        dest.write_text(output, encoding='utf-8')
        print(str(dest))
    else:
        print(output)


if __name__ == '__main__':
    main()
