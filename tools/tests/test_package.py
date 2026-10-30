"""Packaging must preserve live data and keep it out of distributable archives."""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile

PROJECT = Path(__file__).resolve().parents[2]


class PackagingTests(unittest.TestCase):
    def check_packager(self, powershell):
        with tempfile.TemporaryDirectory(prefix='swb-packaging-test-') as temporary:
            root = Path(temporary)
            files = {
                'src/ServerWorkbench.Desktop/ServerWorkbench.Desktop.csproj': '<Project/>',
                'src/ServerWorkbench.Desktop/bin/Release/net48/ServerWorkbench.exe': 'binary fixture',
                'src/ServerWorkbench.Desktop/bin/Release/net48/ServerWorkbench.Core.dll': 'library fixture',
                'README.md': '[说明](docs/使用说明.md)',
                'docs/使用说明.md': '说明',
                'docs/Windows验收.md': '验收',
                'docs/测试报告.md': '报告',
                'docs/内网穿透.md': 'frp relay instructions',
                'FRP-LICENSE.txt': 'Apache License 2.0 fixture',
                'THIRD-PARTY-NOTICES.txt': 'notices',
                'tools/启动检查.cmd': '@echo off',
                'dist/ServerWorkbench/Data/Instances/world/level.dat': 'existing world must survive',
                'dist/ServerWorkbench/Data/Backups/backup.zip': 'existing backup must survive',
                'dist/ServerWorkbench/settings.json': '{"DataRoot":"Data"}',
                'Data/source-world.txt': 'source tree data must not ship',
                'tools/tests/__pycache__/test_package.pyc': 'local interpreter cache must not ship',
            }
            for relative, content in files.items():
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding='utf-8')
            if powershell:
                command = ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(PROJECT/'tools/package.ps1'), '-ProjectRoot', str(root)]
            else:
                shutil.copy2(PROJECT/'tools/package.py', root/'tools/package.py')
                command = [sys.executable, str(root/'tools/package.py')]
            result = subprocess.run(command, capture_output=True, text=True, errors="replace")
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            for relative in [key for key in files if key.startswith('dist/ServerWorkbench/Data/') or key.endswith('settings.json')]:
                self.assertEqual(files[relative], (root/relative).read_text(encoding='utf-8'))
            with zipfile.ZipFile(root/'dist/ServerWorkbench-win-x64-v0.1.zip') as archive:
                names = archive.namelist()
                self.assertIn('ServerWorkbench/ServerWorkbench.exe', names)
                self.assertIn('ServerWorkbench/FRP-LICENSE.txt', names)
                self.assertIn('ServerWorkbench/内网穿透.md', names)
                self.assertFalse(any('/Data/' in name or name.endswith('settings.json') for name in names))
            with zipfile.ZipFile(root/'dist/ServerWorkbench-source-v0.1.zip') as archive:
                self.assertFalse(any('/Data/' in name or '/dist/' in name or '/bin/' in name or '/__pycache__/' in name for name in archive.namelist()))
            manifest = json.loads((root/'dist/build-manifest.json').read_text(encoding='utf-8-sig'))
            self.assertFalse(manifest['windows_execution_verified'])

    def test_python_packager(self):
        self.check_packager(False)

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell required')
    def test_powershell_51_packager(self):
        self.check_packager(True)


if __name__ == '__main__':
    unittest.main()
