import os
"""Compare the C# codec with original KX x64 instructions using RAM-only cases."""
import base64
import json
from pathlib import Path
import secrets
import subprocess
import sys
from emulate_fields import Machine

ROOT = Path(__file__).resolve().parent
DRIVER = Path(sys.argv[1]).resolve() if len(sys.argv) == 2 else ROOT.parent/'expanded/kmfs85o5.dl'
DOTNET = Path(os.environ.get('ISAFOM_DOTNET', 'dotnet'))
PROOF = ROOT/'codec/bin/Release/net10.0/CodecProof.dll'

machine = Machine(DRIVER)
process = subprocess.Popen([str(DOTNET), str(PROOF), str(DRIVER)],
                           stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
passed = 0
try:
    for n in range(64):
        count = [1, 4, 8, 16, 31, 32][n % 6]
        clear = ('0' + ''.join(str(secrets.randbelow(10)) for _ in range(count-1))).encode('ascii')
        encoded_length = [count + 1, 33, 67][n % 3]
        machine.uc.mem_write(machine.INPUT, clear + b'\0')
        machine.uc.mem_write(machine.OUTPUT, b'\0' * 4096)
        result = machine.call(0x44cce0 if machine.kx87 else 0x40c6d0, machine.MODEL + 0x4000, machine.OUTPUT, machine.INPUT, encoded_length)
        if result != encoded_length: raise AssertionError('OriginalEncodeFailed')
        expected = bytes(machine.uc.mem_read(machine.OUTPUT, encoded_length))
        machine.uc.mem_write(machine.INPUT, expected)
        machine.uc.mem_write(machine.OUTPUT, b'\0' * 4096)
        length = machine.call(0x44ce60 if machine.kx87 else 0x40c850, machine.MODEL + 0x4000, machine.OUTPUT, machine.INPUT, encoded_length)
        if length != len(clear) or machine.cstring(machine.OUTPUT) != clear:
            raise AssertionError('OriginalNativeRoundTripFailed')
        process.stdin.write(json.dumps({'Clear':clear.decode('ascii'),
                                       'EncodedBase64':base64.b64encode(expected).decode('ascii')})+'\n')
        process.stdin.flush()
        reply = json.loads(process.stdout.readline())
        if reply != {'MatchesOriginalNativeCode': True}:
            raise AssertionError('CSharpOriginalCodeDifference')
        passed += 1
    process.stdin.close()
    if process.wait(timeout=15) != 0: raise AssertionError('ProofProcessFailed')
    report = {'ComparedCases':passed, 'CSharpMatchesOriginalX64EncryptionAndDecryption':True,
              'OriginalCodeExecutedOnlyInCpuEmulator':True,
              'ValuesTransport':'RAM and private stdin pipe only; never command line/files',
              'RealDriverLoaded':False,'PersonalConfigurationRead':False,'WindowsPersistenceVerified':False,
              'SyntheticValuesOrCiphertextsExported':False,
              'Unverified':'Real GUI representation, scope, persistence, full native adapter and physical printing'}
    report['VersionProfile']='8.7.0917' if machine.kx87 else '8.5.2405'
    destination=ROOT.parent/'kx87-inspect/codec-original-code-proof.json' if machine.kx87 else ROOT/'codec-original-code-proof.json'
    destination.write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps({'passed':passed,'matches_original_code':True,'personal_values_exported':False}))
finally:
    if process.poll() is None:
        process.kill()
        process.wait(timeout=10)
