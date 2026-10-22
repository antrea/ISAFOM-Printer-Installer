"""Two sentinel writes through original exports, with a virtual spooler only.

The opaque context, initialization, printer metadata allocation, registry-read
allocation and post-save housekeeping are explicit fixtures. SetData dispatch,
field routines and SaveSettingsAsDefault/Setup save run original instructions.
No driver is loaded on Windows. No inputs/ciphertexts are written to reports.
"""
import json
from pathlib import Path
import secrets
import struct
import sys
sys.dont_write_bytecode = True
from emulate_fields import Machine


class TrialMachine(Machine):
    HANDLE = 0x11223344
    DM = Machine.MODEL + 0x4000
    STORED_DM = Machine.MODEL + 0x7000
    INFO2 = Machine.MODEL + 0xa000

    def __init__(self, path):
        super().__init__(path)
        self.setup_size = 0x1f24 if self.kx87 else 7968
        self.putq(self.CTX, self.HANDLE)
        self.putq(self.CTX + 0xc0, 0)  # OpenDeviceByName initializes without CPrinterData
        self.putq(self.CTX + 0x48, self.DM)
        self.putq(self.CTX + 0x78, self.DM + 220)
        self.putq(self.INFO2 + 56, self.STORED_DM)
        for address in [self.DM, self.STORED_DM]:
            self.uc.mem_write(address + 68, struct.pack('<HH', 220, 8192))
        self.putd(self.ADMIN, self.setup_size)
        self.putd(self.ADMIN + 4, 0x107)
        self.virtual_store['Setup'] = bytes(self.uc.mem_read(self.ADMIN, self.setup_size))
        self.saved_devmode = bytes(self.uc.mem_read(self.DM, 8412))
        self.fail_devmode_write = False
        self.fail_setup_write = False
        self.uc.mem_map(0x50000000, 0x100000)
        self.heap_next = 0x50000000
        self.hooks[self.image.base + (0x7b9c0 if self.kx87 else 0x79e20)] = lambda: self.ret(self.INFO2)
        self.hooks[self.image.base + (0x503c0 if self.kx87 else 0x4f9c0)] = self.setup_read_fixture
        self.hooks[self.image.base + (0x51bc0 if self.kx87 else 0x511c0)] = lambda: self.ret(1)
        self.hooks[self.image.base + (0x54946c if self.kx87 else 0x50864c)] = lambda: self.ret()
        self.hooks[self.image.base + (0x50570 if self.kx87 else 0x4fb70)] = self.store_value

    def setup_read_fixture(self):
        # No allocated prior blob: the original path proceeds to its regular
        # GetPrinterDataA size probe and low-level store.
        self.putd(self.arg(3), 3)
        self.putd(self.arg(4), 0)
        self.ret(0)

    def store_value(self):
        key = self.cstring(self.arg(2)).decode('ascii')
        if self.arg(1) != self.HANDLE:
            raise ValueError('UnexpectedVirtualPrinterHandle')
        count = self.arg(5)
        if key == 'Setup':
            if count != self.setup_size: raise ValueError('UnexpectedSetupSize')
            if not self.fail_setup_write:
                self.virtual_store[key] = bytes(self.uc.mem_read(self.arg(4), count))
            self.io_calls.append('virtual-store:Setup')
            self.ret(0 if self.fail_setup_write else 1)
        else:
            super().store_value()

    def imported(self, name):
        short = name.split('!')[-1]
        if short == 'SetPrinterW':
            if self.arg(0) != self.HANDLE or self.arg(1) != 2 or self.arg(3) != 0:
                raise ValueError('UnexpectedSetPrinterTarget')
            self.io_calls.append('virtual-SetPrinterW:test-handle-only')
            if not self.fail_devmode_write:
                self.saved_devmode = bytes(self.uc.mem_read(self.q(self.arg(2) + 56), 8412))
            self.ret(0 if self.fail_devmode_write else 1)
        elif short == 'HeapAlloc':
            count = self.arg(2)
            if count > 65536: raise ValueError('UnexpectedHeapAllocation')
            address = self.heap_next
            self.heap_next += (count + 15) & ~15
            if self.heap_next > 0x500f0000: raise ValueError('VirtualHeapBudgetExceeded')
            self.ret(address)
        elif short in ['GetProcessHeap', 'HeapFree']:
            self.ret(1)
        elif short == 'SetPrinterDataW':
            key = self.cstring(self.arg(1), True).decode('utf-16-le')
            if self.arg(0) != self.HANDLE or key != 'KXP_FLAG' or self.arg(2) != 3 or self.arg(4) != 8:
                raise ValueError('UnexpectedProfileProbeWrite')
            self.io_calls.append('virtual-SetPrinterDataW:KXP_FLAG:test-handle-only')
            self.ret(0)
        elif short == 'GetPrinterDataA':
            if self.arg(0) != self.HANDLE or self.cstring(self.arg(1)) != b'Setup':
                raise ValueError('UnexpectedSetupProbe')
            self.putd(self.arg(2), 3)
            self.putd(self.arg(5), self.setup_size)
            self.ret(234)
        else:
            super().imported(name)

    def save(self): return self.call(0x88340 if self.kx87 else 0x85e80, self.CTX)

    def reopen(self):
        self.uc.mem_write(self.ADMIN, self.virtual_store['Setup'])
        self.uc.mem_write(self.DM, self.saved_devmode)


def main():
    root = Path(__file__).parent
    machine = TrialMachine(root.parent / 'expanded' / 'kmfs85o5.dl')
    checks = []
    def check(label, condition):
        checks.append({'check': label, 'passed': bool(condition)})
        if not condition: raise AssertionError(label)

    previous = None
    for round_number in [1, 2]:
        name = ('ISAFOM_SENTINELLA_' + ('A' if round_number == 1 else 'B')).encode('ascii')
        pin = '0' + str(secrets.randbelow(1000)).zfill(3)
        while pin == previous: pin = '0' + str(secrets.randbelow(1000)).zfill(3)
        previous = pin
        check('Round %d native name setter' % round_number, machine.set(3200, name + b'\0') & 255 == 1)
        check('Round %d native account setter without client interface' % round_number, machine.set(3130, pin.encode('ascii') + b'\0') & 255 == 1)
        payload = struct.pack('<IBx', 2, 0) + pin.encode('utf-16-le') + b'\0' * 58
        check('Round %d native storage setter' % round_number, machine.set(80, payload) & 255 == 1)
        check('Round %d native SaveSettingsAsDefault' % round_number, machine.save() == 1)
        machine.reopen()
        ok, actual = machine.get(3200)
        check('Round %d fresh fixture name readback' % round_number, ok & 255 == 1 and actual.split(b'\0')[0] == name)
        # GetData 3130 assumes a non-null client interface in a branch; this
        # OpenDeviceByName fixture has none. The real verifier also compares
        # Setup independently rather than trusting that native getter.
        actual = bytes(machine.uc.mem_read(machine.ADMIN + 0x16, 10))
        check('Round %d fresh stored Setup account readback' % round_number, actual.split(b'\0')[0] == pin.encode('ascii'))
        ok, actual = machine.get(80)
        check('Round %d persisted virtual mode and prompt' % round_number, ok & 255 == 1 and struct.unpack_from('<I', actual)[0] == 2 and actual[4] == 0)

    # The original save can say success even when the storage boundaries fail.
    machine.fail_devmode_write = True
    machine.fail_setup_write = True
    before_setup = machine.virtual_store['Setup']
    machine.set(3200, b'ISAFOM_SENTINELLA_FAILURE\0')
    check('Original save reports success despite simulated failed underlying writes', machine.save() == 1)
    machine.reopen()
    check('Independent stored Setup remains old after failed write', machine.virtual_store['Setup'] == before_setup)
    ok, actual = machine.get(3200)
    check('Fresh readback detects dropped write despite success return', actual.split(b'\0')[0] != b'ISAFOM_SENTINELLA_FAILURE')
    # Verify Unicode queue ABI through the original OpenDeviceByName export.
    opening = Machine(root.parent / 'expanded' / 'kmfs85o5.dl')
    queue = ('ISAFOM_TEST_KX_' + secrets.token_hex(4)).encode('utf-16-le')
    opening.uc.mem_write(opening.INPUT, queue + b'\0\0')
    seen = []
    old_import = opening.imported
    def opening_import(name):
        if name.endswith('!OpenPrinterW'):
            seen.append(opening.cstring(opening.arg(0), True) == queue)
            opening.putq(opening.arg(1), TrialMachine.HANDLE)
            opening.ret(1)
        else: old_import(name)
    opening.imported = opening_import
    def initialized_fixture():
        seen.append(opening.arg(0) == TrialMachine.HANDLE and opening.arg(1) == 0 and opening.arg(2) == 0)
        opening.ret(opening.CTX)
    opening.hooks[opening.image.base + 0x82560] = initialized_fixture
    check('Original OpenDeviceByName accepts UTF16 queue name and reserved zero', opening.call(0x85da0, opening.INPUT, 0) == opening.CTX)
    check('Original open passes only the virtual handle to initialized-context fixture', len(seen) == 2 and all(seen))
    seen.clear()
    check('Original open refuses out-of-range second argument before any I/O', opening.call(0x85da0, opening.INPUT, 125) == 0 and not seen)
    result = {'SchemaVersion': 1, 'Passed': len(checks), 'Checks': checks,
        'BoundaryFixtures': ['opaque initialized context and model', 'printer metadata allocation', 'registry read allocation',
            'post-save housekeeping', 'virtual heap allocation', 'virtual spooler SetPrinterW/Setup/JobId/JobJSId/KXP_FLAG storage',
            'OpenPrinterW and initialized-context fixture', 'documented conversion/logging fixtures from emulate_fields.py'],
        'OriginalInstructions': ['OpenDeviceByName export and validation', 'SetData export dispatch and field setters/getters', 'SaveSettingsAsDefault export and Setup save routine'],
        'OriginalDriverLoadedByWindows': False, 'RealConfigurationWrites': False, 'PhysicalPrintPerformed': False,
        'WindowsPersistenceVerified': False, 'InputValuesOrEncodedPasswordsExported': False}
    (root / 'emulated-trial-save-checks.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps({'passed': len(checks), 'real_configuration_writes': False}))

if __name__ == '__main__': main()
