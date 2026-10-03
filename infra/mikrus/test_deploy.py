"""Run real deployment control flow and archive validation in a disposable sandbox.

Only external system operations are mocked. No root, network or host writes.
Paths in temporary script copies are rebased; production scripts have no test hooks.
"""

import hashlib
import io
import os
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from pathlib import Path

SOURCE = Path(__file__).resolve().parent
MOCK = """#!/usr/bin/env python3
import os, pathlib, subprocess, sys
base = pathlib.Path(os.environ["SANDBOX"])
cmd = pathlib.Path(sys.argv[0]).name
args = sys.argv[1:]
if cmd == "id":
    if args == ["-u"]: print("0")
    else: sys.exit(0 if (base / "user").exists() else 1)
elif cmd == "uname": print("x86_64")
elif cmd == "dpkg-query":
    if (base / "packages").exists(): print("install ok installed")
    else: sys.exit(1)
elif cmd == "apt-get":
    if args[0] == "install": (base / "packages").touch()
elif cmd == "useradd": (base / "user").touch()
elif cmd == "install":
    options = iter(args)
    cleaned = []
    for arg in options:
        if arg in ("-o", "-g"): next(options)
        else: cleaned.append(arg)
    sys.exit(subprocess.call([os.environ["REAL_INSTALL"], *cleaned]))
elif cmd == "df":
    available = 1 if (base / "disk-full").exists() else 5000000
    print("Filesystem")
    print("mock 0 0", available)
elif cmd == "curl":
    current = base / "srv/plansafe/current"
    bad = (current / "web/BAD").exists()
    url = args[-1]
    if ":5001/" in url:
        if bad: sys.exit(22)
        print("OK")
    else:
        import re
        config = (base / "etc/nginx/sites-available/plansafe.conf").read_text()
        port = re.search(r"listen ([0-9]+) ", config)
        if port and not url.startswith("http://127.0.0.1:" + port[1] + "/"):
            sys.exit(7)
        missing = url.endswith(("/api", "/api/unknown", "/_framework/missing.js"))
        print("503" if bad else "404" if missing else "200", end="")
elif cmd == "nginx":
    if (base / "nginx-fail").exists(): sys.exit(1)
elif cmd == "systemctl":
    with (base / "systemctl.log").open("a") as log:
        log.write(" ".join(args) + "\\n")
    if args == ["restart", "systemd-journald"] and (base / "journald-fail").exists():
        sys.exit(23)
    if args == ["restart", "plansafe-api"] and (base / "api-fail-once").exists():
        (base / "api-fail-once").unlink()
        sys.exit(42)
elif cmd == "systemd-detect-virt":
    print("lxc" if (base / "lxc").exists() else "none")
    sys.exit(0 if (base / "lxc").exists() else 1)
elif cmd in ("sleep", "ldd"): pass
else: raise RuntimeError(cmd)
"""


class DeployTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.root = self.base / "srv/plansafe"
        self.scripts = self.base / "scripts"
        self.scripts.mkdir()
        for name in ("deploy.sh", "provision.sh"):
            text = (SOURCE / name).read_text().replace("/srv/plansafe", str(self.root))
            text = text.replace("/etc/", str(self.base / "etc") + "/")
            (self.scripts / name).write_text(text)
        for name in ("archive.py", "nginx.conf", "plansafe-api.service", "config.sh"):
            shutil.copy(SOURCE / name, self.scripts)
        for directory in (
            "nginx/sites-available",
            "nginx/sites-enabled",
            "systemd/system",
        ):
            (self.base / "etc" / directory).mkdir(parents=True)
        (self.base / "etc/os-release").write_text("ID=debian\nVERSION_ID=13\n")
        (self.base / "etc/nginx/sites-enabled/default").write_text("default\n")
        self.bin = self.base / "bin"
        self.bin.mkdir()
        mock = self.bin / "mock"
        compile(MOCK, "mock", "exec")
        mock.write_text(MOCK)
        mock.chmod(0o755)
        for command in (
            "id",
            "uname",
            "dpkg-query",
            "apt-get",
            "useradd",
            "install",
            "df",
            "curl",
            "nginx",
            "systemctl",
            "sleep",
            "ldd",
            "systemd-detect-virt",
        ):
            (self.bin / command).symlink_to(mock)
        real_install = shutil.which("install")
        assert real_install is not None
        self.env = dict(
            os.environ,
            SANDBOX=str(self.base),
            REAL_INSTALL=real_install,
            PATH=f"{self.bin}:{os.environ['PATH']}",
            MIKRUS_HTTP_PORT="20303",
        )

    def invoke(self, script, *args, ok=True):
        result = subprocess.run(
            ["bash", str(self.scripts / script), *args],
            env=self.env,
            capture_output=True,
            text=True,
            timeout=20,
            check=False,
        )
        self.assertEqual(result.returncode == 0, ok, result.stdout + result.stderr)
        return result

    def bundle(self, name="release", bad=False, extra=None):
        path = self.base / (name + ".tar.gz")
        with tarfile.open(path, "w:gz") as archive:
            files = {
                "api/PlanSafe.Api": b"native",
                "api/libe_sqlite3.so": b"sqlite",
                "web/index.html": b"app",
                "web/_framework/dotnet.js": b"runtime",
            }
            if bad:
                files["web/BAD"] = b"broken"
            for filename, content in files.items():
                entry = tarfile.TarInfo(filename)
                entry.size = len(content)
                entry.mode = 0o755 if filename == "api/PlanSafe.Api" else 0o644
                archive.addfile(entry, io.BytesIO(content))
            if extra:
                archive.addfile(extra)
        return path

    def deploy(self, run, *, bad=False, ok=True, archive=None, checksum=None):
        archive = archive or self.bundle(str(run), bad)
        release_id = f"{run}-1-{'a' * 40}"
        checksum = checksum or hashlib.sha256(archive.read_bytes()).hexdigest()
        result = self.invoke("deploy.sh", release_id, checksum, str(archive), ok=ok)
        return release_id, result

    def assert_clean(self):
        self.assertEqual(list(self.root.glob(".config.*")), [])
        self.assertEqual(list((self.root / "releases").glob(".stage.*")), [])
        self.assertFalse((self.root / "current.next").exists())
        self.assertFalse((self.root / "current.rollback").exists())

    def test_repeat_provision_reconciles_config_without_data_loss(self):
        self.invoke("provision.sh")
        data = self.root / "data/plansafe.db"
        data.write_bytes(b"persistent")
        inode = data.stat().st_ino
        config = self.base / "etc/nginx/sites-available/plansafe.conf"
        expected = config.read_bytes()
        config.write_text("drift")
        self.invoke("provision.sh")
        self.assertEqual(config.read_bytes(), expected)
        self.assertEqual(data.read_bytes(), b"persistent")
        self.assertEqual(data.stat().st_ino, inode)
        self.assertFalse((self.base / "etc/nginx/sites-enabled/default").exists())
        self.assertEqual((self.root / "data").stat().st_mode & 0o777, 0o700)

    def test_custom_port_and_lxc_override_repeat(self):
        (self.base / "lxc").touch()
        self.env["MIKRUS_HTTP_PORT"] = "23456"
        self.invoke("provision.sh")
        dropin = (
            self.base
            / "etc/systemd/system/systemd-journald.service.d/10-lxc-credentials.conf"
        )
        self.assertEqual(dropin.read_text(), "[Service]\nImportCredential=\n")
        self.invoke("provision.sh")
        self.assertEqual(dropin.read_text(), "[Service]\nImportCredential=\n")
        config = (self.base / "etc/nginx/sites-available/plansafe.conf").read_text()
        self.assertIn("listen 23456 default_server;", config)
        self.assertIn("listen [::]:23456 default_server;", config)
        self.assertNotIn("listen 80", config)
        calls = (self.base / "systemctl.log").read_text().splitlines()
        self.assertLess(
            calls.index("daemon-reload"), calls.index("restart systemd-journald")
        )

    def test_non_lxc_preserves_unrelated_dropin(self):
        directory = self.base / "etc/systemd/system/systemd-journald.service.d"
        directory.mkdir()
        marker = directory / "other.conf"
        marker.write_text("unrelated")
        self.invoke("provision.sh")
        self.assertEqual(marker.read_text(), "unrelated")
        self.assertFalse((directory / "10-lxc-credentials.conf").exists())
        managed = directory / "10-lxc-credentials.conf"
        managed.write_text("existing administrator config")
        self.invoke("provision.sh")
        self.assertEqual(managed.read_text(), "existing administrator config")

    def test_invalid_port_before_writes(self):
        for port in ("", "80", "22", "5001", "65536", "20303;touch /tmp/x", "020303"):
            with self.subTest(port=port):
                self.env["MIKRUS_HTTP_PORT"] = port
                self.invoke("provision.sh", ok=False)
                self.assertFalse(self.root.exists())

    def test_lxc_dropin_rollback_and_symlink_safety(self):
        (self.base / "lxc").touch()
        self.deploy(1, bad=True, ok=False)
        directory = self.base / "etc/systemd/system/systemd-journald.service.d"
        self.assertFalse(directory.exists())
        target = self.base / "outside"
        target.mkdir()
        directory.symlink_to(target)
        self.invoke("provision.sh", ok=False)
        self.assertEqual(list(target.iterdir()), [])

    def test_lxc_previous_dropin_restored_on_failure(self):
        (self.base / "lxc").touch()
        self.deploy(1)
        directory = self.base / "etc/systemd/system/systemd-journald.service.d"
        override = directory / "10-lxc-credentials.conf"
        override.write_text("previous credential config")
        unrelated = directory / "other.conf"
        unrelated.write_text("unrelated")
        self.deploy(2, bad=True, ok=False)
        self.assertEqual(override.read_text(), "previous credential config")
        self.assertEqual(unrelated.read_text(), "unrelated")

    def test_journald_restart_failure_is_optional_during_provision(self):
        (self.base / "journald-fail").touch()
        result = self.invoke("provision.sh")
        self.assertIn("WARNING: journald log bounds activation failed", result.stderr)
        config = self.base / "etc/systemd/journald.conf.d/plansafe.conf"
        self.assertEqual(
            config.read_text(),
            "[Journal]\nSystemMaxUse=32M\nRuntimeMaxUse=8M\nMaxRetentionSec=7day\n",
        )

    def test_journald_restart_failure_does_not_block_activation(self):
        (self.base / "journald-fail").touch()
        release, result = self.deploy(1)
        self.assertIn("WARNING: journald log bounds activation failed", result.stderr)
        self.assertIn("Activated " + release, result.stdout)
        self.assertEqual(os.readlink(self.root / "current"), "releases/" + release)
        self.assertEqual((self.root / "deployed").read_text(), "1 1\n")
        self.assert_clean()

    def test_journald_rollback_failure_preserves_api_failure_status(self):
        current, _ = self.deploy(1)
        marker = (self.root / "deployed").read_bytes()
        config = self.base / "etc/systemd/journald.conf.d/plansafe.conf"
        config.write_text("previous journal config\n")
        (self.base / "systemctl.log").write_text("")
        (self.base / "journald-fail").touch()
        (self.base / "api-fail-once").touch()
        failed, result = self.deploy(2, ok=False)
        self.assertEqual(result.returncode, 42)
        self.assertIn("WARNING: journald log bounds activation failed", result.stderr)
        self.assertIn("WARNING: journald configuration recovery failed", result.stderr)
        self.assertNotIn("ALERT", result.stderr)
        self.assertEqual(config.read_text(), "previous journal config\n")
        self.assertEqual(os.readlink(self.root / "current"), "releases/" + current)
        self.assertEqual((self.root / "deployed").read_bytes(), marker)
        self.assertFalse((self.root / "releases" / failed).exists())
        self.assertEqual(
            (self.base / "systemctl.log").read_text().splitlines(),
            [
                "daemon-reload",
                "enable nginx plansafe-api",
                "reset-failed systemd-journald",
                "restart systemd-journald",
                "restart plansafe-api",
                "daemon-reload",
                "restart systemd-journald",
                "restart plansafe-api",
                "reload-or-restart nginx",
            ],
        )
        self.assert_clean()

    def test_port_change_rollback_restores_old_listener(self):
        current, _ = self.deploy(1)
        config = self.base / "etc/nginx/sites-available/plansafe.conf"
        previous = config.read_text()
        self.env["MIKRUS_HTTP_PORT"] = "23456"
        self.deploy(2, bad=True, ok=False)
        self.assertEqual(config.read_text(), previous)
        self.assertEqual(os.readlink(self.root / "current"), "releases/" + current)

    def test_activation_retention_stale_and_health_rollback(self):
        first, _ = self.deploy(1)
        data = self.root / "data/plansafe.db"
        data.write_bytes(b"persistent")
        inode = data.stat().st_ino
        second, _ = self.deploy(2)
        third, _ = self.deploy(3)
        self.assertEqual(os.readlink(self.root / "current"), "releases/" + third)
        self.assertFalse((self.root / "releases" / first).exists())
        self.assertTrue((self.root / "releases" / second).is_dir())
        marker = (self.root / "deployed").read_bytes()
        _, result = self.deploy(2)
        self.assertIn("Skipping stale", result.stdout)
        config = self.base / "etc/nginx/sites-available/plansafe.conf"
        config.write_text("previous config")
        failed, result = self.deploy(4, bad=True, ok=False)
        self.assertIn("Post-activation health", result.stderr)
        self.assertNotIn("ALERT", result.stderr)
        self.assertEqual(config.read_text(), "previous config")
        self.assertEqual(os.readlink(self.root / "current"), "releases/" + third)
        self.assertFalse((self.root / "releases" / failed).exists())
        self.assertEqual((self.root / "deployed").read_bytes(), marker)
        self.assertEqual(data.read_bytes(), b"persistent")
        self.assertEqual(data.stat().st_ino, inode)
        self.assert_clean()

    def test_first_activation_failure_leaves_no_current(self):
        release, result = self.deploy(1, bad=True, ok=False)
        self.assertIn("Post-activation health", result.stderr)
        self.assertFalse((self.root / "current").is_symlink())
        self.assertFalse((self.root / "releases" / release).exists())
        self.assertFalse((self.root / "deployed").exists())
        self.assertTrue((self.base / "etc/nginx/sites-enabled/default").exists())
        self.assert_clean()

    def test_pre_activation_failure_and_integrity(self):
        current, _ = self.deploy(1)
        _, result = self.deploy(2, checksum="0" * 64, ok=False)
        self.assertIn("Integrity check failed", result.stderr)
        (self.base / "disk-full").touch()
        _, result = self.deploy(2, ok=False)
        self.assertIn("Insufficient disk", result.stderr)
        self.assertEqual(os.readlink(self.root / "current"), "releases/" + current)
        self.assert_clean()

    def test_reject_symlinked_storage_without_touching_target(self):
        for directory in ("releases", "data"):
            with self.subTest(directory=directory):
                self.root.mkdir(parents=True, exist_ok=True)
                target = self.base / ("outside-" + directory)
                target.mkdir()
                marker = target / "keep"
                marker.write_bytes(b"unrelated")
                link = self.root / directory
                link.symlink_to(target, target_is_directory=True)
                _, result = self.deploy(1, ok=False)
                self.assertIn("Refusing symlinked storage", result.stderr)
                self.assertEqual(marker.read_bytes(), b"unrelated")
                link.unlink()

    def test_reject_incomplete_browser_artifact(self):
        archive = self.base / "incomplete.tar.gz"
        with (
            tarfile.open(self.bundle(), "r:gz") as source,
            tarfile.open(archive, "w:gz") as output,
        ):
            for member in source:
                if member.name != "web/_framework/dotnet.js":
                    output.addfile(member, source.extractfile(member))
        _, result = self.deploy(1, archive=archive, ok=False)
        self.assertIn("Missing regular file: web/_framework/dotnet.js", result.stderr)
        self.assertFalse((self.root / "current").exists())

    def test_nginx_validation_failure_restores_configuration(self):
        current, _ = self.deploy(1)
        config = self.base / "etc/nginx/sites-available/plansafe.conf"
        config.write_text("previous config")
        (self.base / "nginx-fail").touch()
        self.deploy(2, ok=False)
        self.assertEqual(config.read_text(), "previous config")
        self.assertEqual(os.readlink(self.root / "current"), "releases/" + current)
        self.assert_clean()

    def test_reject_unsafe_archives_before_extraction(self):
        for name, kind in (
            ("web/../../escape", tarfile.REGTYPE),
            ("web/link", tarfile.SYMTYPE),
            ("web/hard", tarfile.LNKTYPE),
            ("web/index.html", tarfile.REGTYPE),
            ("/absolute", tarfile.REGTYPE),
            ("web/device", tarfile.CHRTYPE),
        ):
            with self.subTest(name=name):
                entry = tarfile.TarInfo(name)
                entry.type = kind
                entry.linkname = (
                    "/etc/passwd" if kind in (tarfile.SYMTYPE, tarfile.LNKTYPE) else ""
                )
                archive = self.bundle(extra=entry)
                result = subprocess.run(
                    ["python3", str(SOURCE / "archive.py"), str(archive)],
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("Unsafe archive entry", result.stderr)
        self.assertFalse((self.base / "escape").exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
