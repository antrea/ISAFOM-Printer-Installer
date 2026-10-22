"""Local REA MCP runner. Reuses installed tools; writes only in this folder."""
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time

ROOT = Path(__file__).resolve().parent
NODE = Path(os.environ.get('ISAFOM_NODE', 'node'))
REA = Path(os.environ['ISAFOM_REA_ENTRY']).resolve()


class Rea:
    def __init__(self):
        env = os.environ.copy()
        env.update({
            'TEMP': str(ROOT/'tmp'), 'TMP': str(ROOT/'tmp'), 'TMPDIR': str(ROOT/'tmp'),
            'USERPROFILE': str(ROOT/'rea-home'), 'APPDATA': str(ROOT/'rea-home/AppData/Roaming'),
            'LOCALAPPDATA': str(ROOT/'rea-home/AppData/Local'),
            # GHIDRA_INSTALL_DIR and JAVA_HOME are inherited from the environment.
            'REA_ANALYSIS_PROVIDER': 'ghidra',
            'JAVA_TOOL_OPTIONS': f'-Djava.io.tmpdir="{ROOT / "tmp"}" -Duser.home="{ROOT / "rea-home"}" -XX:-UsePerfData',
        })
        self.errors = (ROOT/'rea-native.stderr.log').open('w', encoding='utf-8')
        self.child = subprocess.Popen([str(NODE), str(REA), 'mcp'], cwd=ROOT, env=env,
                                      stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.errors,
                                      text=True, encoding='utf-8', bufsize=1, creationflags=subprocess.CREATE_NO_WINDOW)
        self.responses = queue.Queue()
        (ROOT/'rea-owned-process.json').write_text(json.dumps({'pid': self.child.pid, 'entry': str(REA)}), encoding='utf-8')
        self.next_id = 0
        threading.Thread(target=self.read, daemon=True).start()

    def read(self):
        for line in self.child.stdout:
            try: self.responses.put(json.loads(line))
            except json.JSONDecodeError: pass

    def send(self, message):
        self.child.stdin.write(json.dumps(message)+'\n')
        self.child.stdin.flush()

    def call(self, method, params, timeout=360):
        self.next_id += 1
        request_id = self.next_id
        self.send({'jsonrpc': '2.0', 'id': request_id, 'method': method, 'params': params})
        deadline = time.monotonic() + timeout
        while True:
            response = self.responses.get(timeout=max(.1, deadline-time.monotonic()))
            if response.get('id') == request_id: return response

    def initialize(self):
        result = self.call('initialize', {'protocolVersion': '2025-03-26', 'capabilities': {},
                                         'clientInfo': {'name': 'isafom-kx-static-analysis', 'version': '1.0'}})
        self.send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})
        return result

    def close(self):
        self.child.stdin.close()
        try: self.child.wait(timeout=45)
        except subprocess.TimeoutExpired:
            # Stop this owned analysis server only; no driver process is run.
            self.child.terminate()
            self.child.wait(timeout=10)
        self.errors.close()


if __name__ == '__main__':
    rea = Rea()
    try:
        rea.initialize()
        catalog = rea.call('tools/list', {})
        selected = [t for t in catalog.get('result', {}).get('tools', [])
                    if t['name'] in ['open_binary', 'batch_decompile', 'close_binary', 'analyze_function', 'procedure_pseudo_code', 'search_strings']]
        (ROOT/'rea-native-tools.json').write_text(json.dumps(selected, indent=2), encoding='utf-8')
        print(json.dumps(selected), flush=True)
    finally: rea.close()
