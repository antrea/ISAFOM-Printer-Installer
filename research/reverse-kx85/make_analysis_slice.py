"""Create a non-runnable, explicitly synthetic PE analysis slice for REA.

Selected routines and their data keep original RVAs and exact instruction
bytes. Other executable bytes become INT3; omitted direct callees get RET
stubs. Entry point and unrelated exports/exception roots are removed. This
derivative is never a driver to load/install, or evidence of omitted effects.
The unmodified original remains the source of emulator/native-code checks.
"""
import hashlib
import json
from pathlib import Path
import struct
from analyze_native import Image
from capstone.x86_const import X86_OP_IMM

ROOT = Path(__file__).resolve().parent
ORIGINAL = ROOT.parent/'expanded/kmfs85o5.dl'
im = Image(ORIGINAL)
selected = [0x85e20, 0x85e50, 0x85e80, 0x82710, 0x83f10, 0x85c20,
            0x73830, 0x73900, 0x71870, 0x71be0, 0x60e20, 0x61160,
            0x7e320, 0x7e500, 0x7a030, 0x784c0, 0x685d0, 0x65d00,
            0x51ca0, 0x532a44, 0x40c6d0, 0x40c850, 0x40ca50,
            0x40cac0, 0x40cbe0]
regions = [im.bounds(rva) for rva in selected]
regions += [(0x40c6a0, 0x40c6d0), (0x5083fa, 0x508600)]
data = bytearray(im.data)
for section in im.pe.sections:
    if section.Characteristics & 0x20000000:
        begin, length = section.PointerToRawData, section.SizeOfRawData
        data[begin:begin+length] = b'\xcc'*length
for begin, end in regions:
    offset = im.pe.get_offset_from_rva(begin)
    data[offset:offset+end-begin] = im.pe.get_data(begin, end-begin)

stubbed = set()
for begin, end in regions:
    for ins in im.md.disasm(im.pe.get_data(begin, end-begin), im.base+begin):
        if ins.mnemonic == 'call' and ins.operands[0].type == X86_OP_IMM:
            target = ins.operands[0].imm-im.base
            section = im.pe.get_section_by_rva(target) if 0 <= target < im.pe.OPTIONAL_HEADER.SizeOfImage else None
            if section is not None and section.Characteristics & 0x20000000 and not any(a <= target < b for a,b in regions):
                offset = im.pe.get_offset_from_rva(target)
                data[offset] = 0xc3
                stubbed.add(target)

pe = im.pe
def setd(offset, value): data[offset:offset+4] = struct.pack('<I', value)
setd(pe.OPTIONAL_HEADER.get_field_absolute_offset('AddressOfEntryPoint'), 0)
for symbol in pe.DIRECTORY_ENTRY_EXPORT.symbols:
    if not any(a <= symbol.address < b for a,b in regions):
        setd(pe.get_offset_from_rva(pe.DIRECTORY_ENTRY_EXPORT.struct.AddressOfFunctions) +
             (symbol.ordinal-pe.DIRECTORY_ENTRY_EXPORT.struct.Base)*4, 0)
selected_exceptions = [x for x in pe.DIRECTORY_ENTRY_EXCEPTION
                       if any(a <= x.struct.BeginAddress < b for a,b in regions)]
exception_dir = pe.OPTIONAL_HEADER.DATA_DIRECTORY[3]
exception_offset = pe.get_offset_from_rva(exception_dir.VirtualAddress)
data[exception_offset:exception_offset+exception_dir.Size] = b'\0'*exception_dir.Size
for index, row in enumerate(selected_exceptions):
    data[exception_offset+index*12:exception_offset+(index+1)*12] = row.struct.__pack__()
setd(exception_dir.get_field_absolute_offset('Size'), 12*len(selected_exceptions))

dest = ROOT/'kx_fields_ANALYSIS_ONLY.dll'
dest.write_bytes(data)
manifest = {'SourceSha256': hashlib.sha256(im.data).hexdigest(),
            'DerivativeSha256': hashlib.sha256(data).hexdigest(),
            'NeverExecuteOrInstall': True, 'ModifiedMetadataAndOmittedCode': True,
            'OmittedDirectCalleesAreStubs': [hex(x) for x in sorted(stubbed)],
            'OriginalCodeRegions': [{'begin_rva': hex(a), 'end_rva': hex(b),
                'sha256': hashlib.sha256(im.pe.get_data(a,b-a)).hexdigest(),
                'derivative_bytes_match': data[pe.get_offset_from_rva(a):pe.get_offset_from_rva(a)+b-a] == im.pe.get_data(a,b-a)}
                 for a,b in regions]}
(ROOT/'analysis-slice-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print(json.dumps({'file':str(dest),'regions':len(regions),'stubbed_omitted_callees':len(stubbed),
                  'retained_bytes_match':all(x['derivative_bytes_match'] for x in manifest['OriginalCodeRegions'])}))
