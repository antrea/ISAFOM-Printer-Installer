"""Run exact original KX field routines in a bounded x64 emulator.

No Windows DLL loader, native driver execution, real printer/registry/network
API, or personal input is used. All identities and codes are generated in RAM.
The object/model and persistence are fixtures, not an installed printer. Only
the explicitly listed logging/I/O/conversion boundaries are replaced. This is
evidence about field routines, not proof of Windows persistence or printing.
"""
import argparse
import hashlib
import json
from pathlib import Path
import secrets
import struct
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).parent / 'vendor'))
import unicorn as u
from unicorn.x86_const import *
from analyze_native import Image

EXPECTED_HASH = '9fcc2d8db6851fa59b0d8d5794f1980627b122abbf154159793f8f46fafa1cd1'
KX87_HASH = '6805c42c9b92f52244909b0700727d239da81c30a1a4a1638fa2b46e3aa02358'


class Machine:
    CTX = 0x20000000
    ADMIN = 0x21000000
    PRIVATE = 0x22000000
    MODEL = 0x23000000
    INPUT = 0x24000000
    OUTPUT = 0x25000000
    STACK = 0x30000000
    RETURN = 0x400ff000

    def __init__(self, path):
        self.image = Image(path)
        digest = hashlib.sha256(self.image.data).hexdigest()
        if digest not in [EXPECTED_HASH, KX87_HASH]:
            raise ValueError('ExactOriginalDriverHashRequired')
        self.kx87 = digest == KX87_HASH
        self.uc = u.Uc(u.UC_ARCH_X86, u.UC_MODE_64)
        size = (self.image.pe.OPTIONAL_HEADER.SizeOfImage + 4095) & ~4095
        self.uc.mem_map(self.image.base, size)
        self.uc.mem_write(self.image.base, self.image.pe.get_memory_mapped_image())
        for address, size in [(self.CTX, 0x10000), (self.ADMIN, 0x10000), (self.PRIVATE, 0x10000),
                              (self.MODEL, 0x10000), (self.INPUT, 0x10000), (self.OUTPUT, 0x10000),
                              (self.STACK, 0x100000), (0x40000000, 0x100000)]:
            self.uc.mem_map(address, size)
        self.putq(self.CTX + 0x1d8, self.ADMIN)
        self.putq(self.CTX + 0x78, self.PRIVATE)
        self.putq(self.CTX + 0x30, self.MODEL)
        self.putq(self.MODEL + 0xc0, self.MODEL + 0x1000)
        self.putq(self.CTX + 0xc0, self.MODEL + 0x2000)
        self.putq(self.MODEL + 0x2000, self.MODEL + 0x3000)
        self.putq(self.MODEL + 0x3000 + 0x128, 0x400fe000)
        self.putq(self.MODEL + 0x3000 + 0x130, 0x400fe010)
        # The fixture contains no real printer handle. Native lock/profile
        # checks therefore take their empty-context paths, not an admin bypass.
        self.virtual_store = {}
        self.io_calls = []
        self.last_status = None
        self.hooks = {self.image.base + (0x7b800 if self.kx87 else 0x79c60): self.log,
                      self.image.base + (0x50570 if self.kx87 else 0x4fb70): self.store_value,
                      0x400fe000: self.client_override_missing,
                      0x400fe010: self.client_write}
        for n, (rva, name) in enumerate(self.image.imports.items()):
            address = 0x40000000 + n * 16
            self.putq(self.image.base + rva, address)
            self.hooks[address] = lambda name=name: self.imported(name)
        self.uc.hook_add(u.UC_HOOK_CODE, self.code)

    def q(self, address): return struct.unpack('<Q', self.uc.mem_read(address, 8))[0]
    def putq(self, address, value): self.uc.mem_write(address, struct.pack('<Q', value))
    def putd(self, address, value): self.uc.mem_write(address, struct.pack('<I', value))
    def reg(self, number): return self.uc.reg_read(number)
    def arg(self, n):
        return self.reg([UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9][n]) if n < 4 else self.q(self.reg(UC_X86_REG_RSP) + 0x28 + 8 * (n - 4))

    def cstring(self, address, wide=False):
        result = bytearray()
        step = 2 if wide else 1
        for i in range(1024):
            item = bytes(self.uc.mem_read(address + i * step, step))
            if item == b'\0' * step: return bytes(result)
            result.extend(item)
        raise ValueError('UnterminatedEmulatedString')

    def ret(self, value=0):
        sp = self.reg(UC_X86_REG_RSP)
        destination = self.q(sp)
        self.uc.reg_write(UC_X86_REG_RAX, value & 0xffffffffffffffff)
        self.uc.reg_write(UC_X86_REG_RSP, sp + 8)
        self.uc.reg_write(UC_X86_REG_RIP, destination)

    def code(self, uc, address, size, data):
        if address in self.hooks: self.hooks[address]()

    def log(self):
        self.last_status = self.arg(2)
        self.ret()

    def store_value(self):
        key = self.cstring(self.arg(2)).decode('ascii')
        count = self.arg(5)
        if key not in ['JobId', 'JobJSId'] or count > 512:
            raise ValueError('UnexpectedVirtualStoreWrite')
        self.virtual_store[key] = bytes(self.uc.mem_read(self.arg(4), count))
        self.io_calls.append('virtual-store:' + key)
        self.ret(1)

    def client_override_missing(self):
        self.io_calls.append('virtual-client-override:missing')
        self.ret(1)

    def client_write(self):
        self.io_calls.append('virtual-client-identity-write:discarded')
        self.ret(0)

    def imported(self, name):
        self.io_calls.append(name)
        short = name.split('!')[-1]
        if short == 'GetACP':
            self.ret(1252)
        elif short == 'GetPrinterA':
            self.putd(self.arg(4), 0)
            self.ret(0)
        elif short == 'GetPrinterDataW':
            key = self.cstring(self.arg(1), True).decode('utf-16-le')
            value = self.virtual_store.get(key, b'')
            self.putd(self.arg(5), len(value))
            if self.arg(2): self.putd(self.arg(2), 1)
            if self.arg(3) and self.arg(4) >= len(value):
                self.uc.mem_write(self.arg(3), value)
                self.ret(0)
            else: self.ret(234 if value else 2)
        elif short in ['WideCharToMultiByte', 'MultiByteToWideChar']:
            if self.arg(0) not in [0, 1252, 65001]:
                raise ValueError('UnsupportedCodePageFixture')
            encoding = 'utf-8' if self.arg(0) == 65001 else 'cp1252'
            source, count = self.arg(2), self.arg(3)
            if short == 'WideCharToMultiByte':
                raw = self.cstring(source, True) + b'\0\0' if count in [0xffffffff, 0xffffffffffffffff] else bytes(self.uc.mem_read(source, count * 2))
                output = raw.decode('utf-16-le').encode(encoding)
                units = len(output)
            else:
                raw = self.cstring(source) + b'\0' if count in [0xffffffff, 0xffffffffffffffff] else bytes(self.uc.mem_read(source, count))
                output = raw.decode(encoding).encode('utf-16-le')
                units = len(output) // 2
            if not self.arg(4): self.ret(units)
            elif units <= self.arg(5):
                self.uc.mem_write(self.arg(4), output)
                self.ret(units)
            else: self.ret(0)
        else:
            raise ValueError('UnmodelledExternalCall:' + name)

    def call(self, rva, *args):
        sp = self.STACK + 0xff000 - 8
        self.putq(sp, self.RETURN)
        for n, value in enumerate(args):
            if n < 4:
                self.uc.reg_write([UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9][n], value)
            else: self.putq(sp + 0x28 + 8 * (n - 4), value)
        self.uc.reg_write(UC_X86_REG_RSP, sp)
        self.uc.emu_start(self.image.base + rva, self.RETURN, timeout=5_000_000, count=2_000_000)
        if self.reg(UC_X86_REG_RIP) != self.RETURN:
            raise ValueError('EmulationBudgetExceeded')
        return self.reg(UC_X86_REG_RAX) & 0xffffffff

    def set(self, key, value):
        self.uc.mem_write(self.INPUT, b'\0' * 4096)
        self.uc.mem_write(self.INPUT, value)
        return self.call(0x882e0 if self.kx87 else 0x85e20, self.CTX, key, self.INPUT)

    def get(self, key):
        self.uc.mem_write(self.OUTPUT, b'\0' * 4096)
        result = self.call(0x88310 if self.kx87 else 0x85e50, self.CTX, key, self.OUTPUT)
        return result, bytes(self.uc.mem_read(self.OUTPUT, 512))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('path')
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    dest = Path(args.output).resolve()
    if not dest.is_relative_to(Path(__file__).parent.resolve()):
        raise ValueError('OutputMustStayInAnalysisFolder')
    machine = Machine(args.path)
    checks = []

    def check(label, result):
        checks.append({'check': label, 'passed': bool(result)})
        if not result: raise AssertionError(label)

    name = ('TEST_' + secrets.token_hex(8)).encode('ascii')
    check('Native SetData 3200 accepts synthetic name', machine.set(3200, name + b'\0') == 1)
    ok, actual = machine.get(3200)
    check('Native GetData 3200 returns exact synthetic name', ok == 1 and actual.split(b'\0')[0] == name)
    for count in [1, 4, 8]:
        code = ('0' + ''.join(str(secrets.randbelow(10)) for _ in range(count-1))).encode('ascii')
        check(f'Native SetData 3130 accepts {count} ASCII digits', machine.set(3130, code + b'\0') == 1)
        ok, actual = machine.get(3130)
        check(f'Native GetData 3130 preserves {count} digits including leading zeros', ok == 1 and actual.split(b'\0')[0] == code)
    for label, invalid in [('letter', b'0x'), ('nine digits', b'0' * 9)]:
        check('Native account rejects ' + label, machine.set(3130, invalid + b'\0') == 0)
    pin = '0' + ''.join(str(secrets.randbelow(10)) for _ in range(3))
    payload = struct.pack('<IBx', 2, 0) + pin.encode('utf-16-le') + b'\0\0' * 29
    check('Native SetData 80 accepts storage structure and fixed code', machine.set(80, payload) == 1)
    ok, actual = machine.get(80)
    check('Native GetData 80 returns exact requested storage mode', ok == 1 and struct.unpack_from('<I', actual)[0] == 2)
    check('Native GetData 80 returns password prompt disabled', actual[4] == 0)
    # Do not conceal the observed getter defect by giving the Windows conversion
    # mock different semantics. The original passes 67 input bytes to a 33-wide
    # destination. With a short ASCII PIN and zero padding, conversion fails.
    native_password_matches = actual[6:72].decode('utf-16-le').split('\0')[0] == pin
    check('Native GetData 80 reproduces the empty password readback', not native_password_matches and actual[6:8] == b'\0\0')
    check('Native storage writes two matching virtual representations',
          bool(machine.virtual_store.get('JobId')) and machine.virtual_store.get('JobId') == machine.virtual_store.get('JobJSId'))
    encoded = machine.virtual_store['JobId']
    machine.uc.mem_write(machine.INPUT, encoded)
    machine.uc.mem_write(machine.OUTPUT, b'\0' * 512)
    decoded_length = machine.call(0x40c850, machine.MODEL + 0x4000, machine.OUTPUT, machine.INPUT, len(encoded))
    check('Original native decoder recovers the exact synthetic code from the virtual representation',
          decoded_length == len(pin) and machine.cstring(machine.OUTPUT).decode('utf-8') == pin)
    report = {'SchemaVersion': 1, 'DriverSha256': EXPECTED_HASH,
              'Environment': 'Unicorn x64 CPU emulator; original instructions; synthetic object/model/persistence fixtures',
              'Passed': len(checks), 'Checks': checks,
              'AllowedBoundaryReplacements': ['error logging', 'low-level virtual password store',
                                               'client identity I/O (getter override absent)', 'GetPrinterA', 'GetPrinterDataW', 'GetACP=1252; UTF-8/CP1252/UTF-16 Windows conversion'],
              'ExternalCallsObserved': sorted(set(machine.io_calls)),
              'PrivateRepresentationBytes': {key: len(value) for key, value in machine.virtual_store.items()},
              'OriginalDriverLoadedByWindows': False, 'RealConfigurationWrites': False,
              'PersonalDataRead': False, 'NativeCodeOnHostExecuted': False,
              'InputValuesOrEncodedPasswordsExported': False, 'WindowsPersistenceVerified': False,
              'StorageMode2SemanticLabelVerified': False, 'ProductionAdapterEnabled': False}
    report['NativeGetData80PrivateCodeMatchesRequested'] = native_password_matches
    report['NativePrivateRepresentationDecoderRoundTripVerified'] = True
    dest.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({'passed': len(checks), 'report': str(dest), 'real_configuration_writes': False}))


if __name__ == '__main__':
    main()
