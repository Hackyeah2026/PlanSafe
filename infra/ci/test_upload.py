"""Exercise upload validation and post-activation public checks without network."""

import os
import subprocess
import tempfile
import unittest
from pathlib import Path

SOURCE = Path(__file__).resolve().parent
MOCK = """#!/usr/bin/env python3
import os, pathlib, sys
base = pathlib.Path(os.environ['SANDBOX'])
cmd = pathlib.Path(sys.argv[0]).name
args = sys.argv[1:]
with (base / 'calls').open('a') as log:
    log.write(cmd + ' ' + ' '.join(args) + '\\n')
if cmd == 'ssh':
    if 'bash' in args:
        sys.stdin.read()
        if os.environ.get('STALE'): print('Skipping stale/already deployed run')
        else:
            (base / 'activated').touch()
            print('Activated ' + os.environ['RELEASE_ID'])
    elif 'mktemp' in args[-1]: print('/root/plansafe.ABCD1234')
elif cmd == 'curl':
    if os.environ.get('PUBLIC_FAIL'): sys.exit(7)
    body = 'OK' if args[-1].endswith('/api/health') else '<html>PlanSafe</html>'
    if os.environ.get('WRONG_BODY'): body = '<html>other app</html>'
    pathlib.Path(args[args.index('-o') + 1]).write_text(body)
    print(os.environ.get('PUBLIC_STATUS', '200'), end='')
"""


class UploadTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.base = Path(temp.name)
        binary = self.base / "bin"
        binary.mkdir()
        mock = binary / "mock"
        mock.write_text(MOCK)
        mock.chmod(0o755)
        for command in ("ssh", "scp", "curl", "sleep"):
            (binary / command).symlink_to(mock)
        artifacts = self.base / "artifacts"
        artifacts.mkdir()
        for name in ("deploy-bundle.tar.gz", "release.tar.gz"):
            (artifacts / name).write_bytes(b"mock")
        self.env = dict(
            os.environ,
            PATH=f"{binary}:{os.environ['PATH']}",
            SANDBOX=str(self.base),
            MIKRUS_HOST="example.test",
            MIKRUS_SSH_PORT="10303",
            MIKRUS_HTTP_PORT="20303",
            MIKRUS_PUBLIC_URL="https://maluch3-20303.wykr.es",
            MIKRUS_KEY="mock",
            MIKRUS_KNOWN_HOSTS="mock",
            RELEASE_ID="1-1-" + "a" * 40,
        )

    def invoke(self):
        return subprocess.run(
            ["bash", str(SOURCE / "upload.sh")],
            cwd=self.base,
            env=self.env,
            capture_output=True,
            text=True,
            timeout=15,
            check=False,
        )

    def test_invalid_configuration_never_calls_ssh(self):
        for key, values in {
            "MIKRUS_HTTP_PORT": ("", "5001", "10303", "65536", "20303;id"),
            "MIKRUS_PUBLIC_URL": (
                "",
                "http://example.test",
                "https://u@example.test",
                "https://example.test/a",
                "https://example.test?q=a",
                "https://example.test#f",
                "https://example.test:65536",
                "https://example.test;id",
            ),
        }.items():
            original = self.env[key]
            for value in values:
                with self.subTest(value=value):
                    self.env[key] = value
                    self.assertNotEqual(self.invoke().returncode, 0)
                    self.assertFalse((self.base / "calls").exists())
            self.env[key] = original

    def test_success_and_dedicated_subdomain(self):
        self.env["MIKRUS_PUBLIC_URL"] = "https://name.bieda.it/"
        result = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = (self.base / "calls").read_text()
        self.assertIn("20303 10303", calls)
        self.assertIn("https://name.bieda.it/api/health", calls)
        self.assertNotIn(" -k ", calls)
        self.assertNotIn(" --location ", calls)

    def test_public_failure_does_not_rollback_activation(self):
        self.env["PUBLIC_FAIL"] = "1"
        result = self.invoke()
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue((self.base / "activated").exists())
        self.assertIn("NOT rolled back", result.stderr)
        calls = (self.base / "calls").read_text()
        self.assertEqual(calls.count("curl "), 6)
        self.assertEqual(calls.count("ssh "), 3)  # create, activate, cleanup only

    def test_redirect_and_wrong_content_rejected(self):
        for key, value in (("PUBLIC_STATUS", "302"), ("WRONG_BODY", "1")):
            with self.subTest(key=key):
                self.env[key] = value
                result = self.invoke()
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("NOT rolled back", result.stderr)
                self.env.pop(key)

    def test_stale_remote_skip_has_no_public_check(self):
        self.env["STALE"] = "1"
        self.assertEqual(self.invoke().returncode, 0)
        self.assertNotIn("curl ", (self.base / "calls").read_text())


if __name__ == "__main__":
    unittest.main(verbosity=2)
