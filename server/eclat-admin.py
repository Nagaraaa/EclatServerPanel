#!/usr/bin/python3
"""Bounded SSH administration for one Eclat Originel systemd service."""
from __future__ import annotations

import base64
import contextlib
import datetime as dt
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import socket
import sqlite3
import stat
import subprocess
import sys
import tarfile
import tempfile
import time

CONFIG_PATH = Path('/etc/eclat-panel.json')
MAX_REQUEST = 16384
MAX_DOWNLOAD = 64 * 1024 * 1024
ACTIONS = {'status', 'logs', 'start', 'stop', 'restart', 'backup', 'backups', 'download', 'restore', 'diagnostics'}
SNAPSHOT_NAME = re.compile(r'^eclat-\d{8}T\d{6}Z-[a-f0-9]{8}\.tar\.gz$')
SECRET_KEY = re.compile(r'(?:password|passwd|secret|token|authorization|credential|private.?key|publish.?key|api.?key|auth.?key|session.?key)', re.I)


def redact(value):
    if isinstance(value, dict):
        return {str(k): '[masqué]' if SECRET_KEY.search(str(k)) else redact(v) for k, v in value.items()}
    if isinstance(value, list):
        return [redact(v) for v in value]
    if isinstance(value, str):
        # URLs may carry credentials even when the surrounding log key is innocuous.
        value = re.sub(r'(?i)(\b(?:password|passwd|secret|token|authorization|credential|publishKey|apiKey)[\w-]*[\s"\x27]*[:=][\s"\x27]*)([^\s,;"\x27}]+)', r'\1[masqué]', value)
        value = re.sub(r'(?i)([?&](?:token|key|secret|password|auth)=)[^&\s]+', r'\1[masqué]', value)
        value = re.sub(r'(?i)(https?://)[^/@\s]+:[^/@\s]+@', r'\1[masqué]@', value)
        value = re.sub(r'-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----', '[clé privée masquée]', value)
        return value
    return value


class Admin:
    def __init__(self, config):
        self.config = config
        self.data = Path(config.get('dataDir', '/var/lib/eclat-originel')).resolve()
        self.database = self.data / config.get('database', 'coop.sqlite')
        if self.database.parent != self.data:
            raise ValueError('Invalid database path')
        self.backup_dir = Path(config.get('backupDir', '/var/backups/eclat-originel')).resolve()
        self.service = config.get('service', 'eclat-server.service')
        if not re.fullmatch(r'[A-Za-z0-9@_.-]+\.service', self.service):
            raise ValueError('Invalid service name')
        self.private_files = config.get('privateFiles', ['server.json', 'coop.sqlite.identity.json', 'eclats.json', 'coop-publish.json', 'internet-access.json', 'cloudflared-quick.yml'])
        for name in self.private_files:
            if Path(name).name != name or name in {'.', '..'}:
                raise ValueError('Invalid snapshot configuration path')

    def command(self, *args, timeout=15, check=True):
        result = subprocess.run(args, capture_output=True, text=True, timeout=timeout, check=False)
        if check and result.returncode:
            raise RuntimeError(f'{Path(args[0]).name} failed: {redact(result.stderr.strip()[-1000:])}')
        return result.stdout.strip()

    def service_state(self):
        raw = self.command('/usr/bin/systemctl', 'show', self.service, '--property=ActiveState,SubState,MainPID,ControlGroup', check=False)
        fields = dict(line.split('=', 1) for line in raw.splitlines() if '=' in line)
        return {'active': fields.get('ActiveState', 'unknown'), 'sub': fields.get('SubState', 'unknown'), 'pid': int(fields.get('MainPID', '0') or 0), 'controlGroup': fields.get('ControlGroup', '')}

    def protocol(self):
        started = time.monotonic()
        try:
            with socket.create_connection(('127.0.0.1', int(self.config.get('port', 8787))), timeout=4) as connection:
                connection.settimeout(4)
                self.probe_connection(connection)
            return {'ok': True, 'latencyMs': round((time.monotonic() - started) * 1000, 1), 'error': None}
        except (OSError, ValueError, EOFError, RuntimeError, json.JSONDecodeError) as exc:
            return {'ok': False, 'latencyMs': None, 'error': str(exc)[:300]}

    def probe_connection(self, connection):
        """Native newline JSON game protocol; two replies prove the event loop responds."""
        stream = connection.makefile('rwb', buffering=0)
        for payload, expected in (({'v': 1, 'id': 'admin_hello', 'type': 'hello', 'payload': {'name': 'AdminCheck'}}, 'hello.ok'), ({'v': 1, 'id': 'admin_ping', 'type': 'ping', 'payload': {}}, 'ping.ok')):
            stream.write(json.dumps(payload).encode() + b'\n')
            reply = stream.readline(65537)
            if not reply or len(reply) > 65536:
                raise RuntimeError('No valid game response')
            message = json.loads(reply)
            if message.get('type') != expected:
                raise RuntimeError(f'Unexpected game reply: {message.get("type", "missing type")}')

    def metrics(self):
        memory = {}
        with open('/proc/meminfo', encoding='ascii') as source:
            for line in source:
                key, value = line.split(':', 1)
                memory[key] = int(value.strip().split()[0]) * 1024
        disk = shutil.disk_usage(self.data)
        return {'memoryTotalBytes': memory.get('MemTotal', 0), 'memoryAvailableBytes': memory.get('MemAvailable', 0), 'diskTotalBytes': disk.total, 'diskFreeBytes': disk.free, 'load': list(os.getloadavg())}

    @staticmethod
    def monitor_process_current(monitor, service):
        # MainPID may be a supervisor: the game's Node child must belong to the
        # same service, rather than have the supervisor's exact PID.
        pid = monitor.get('pid')
        group = service.get('controlGroup') if isinstance(service, dict) else None
        if (type(pid) is not int or pid <= 0 or not isinstance(group, str)
                or not group.startswith('/') or group.rstrip('/') == ''
                or service.get('active') != 'active'):
            return False
        group = group.rstrip('/')
        try:
            lines = Path(f'/proc/{pid}/cgroup').read_text(encoding='utf-8').splitlines()
        except (OSError, ValueError):
            return False
        for line in lines:
            fields = line.split(':', 2)
            if len(fields) != 3:
                continue
            hierarchy, controllers, membership = fields
            if not ((hierarchy == '0' and controllers == '') or 'name=systemd' in controllers.split(',')):
                continue
            if membership == group or membership.startswith(group + '/'):
                return True
        return False

    def monitor(self, service=None):
        path = self.data / 'monitor.json'
        try:
            if not path.is_file():
                return {}
            if path.stat().st_size > 2 * 1024 * 1024:
                return {'error': 'Monitor file too large'}
            snapshot = json.loads(path.read_text(encoding='utf-8'))
            if not isinstance(snapshot, dict):
                return {'error': 'Monitor unavailable'}
            if service is None:
                try:
                    service = self.service_state()
                except (OSError, ValueError, subprocess.SubprocessError):
                    service = {}
            snapshot['processCurrent'] = self.monitor_process_current(snapshot, service)
            return redact(snapshot)
        except (OSError, ValueError):
            return {'error': 'Monitor unavailable'}

    def status(self):
        with open('/proc/uptime', encoding='ascii') as source:
            uptime = float(source.read().split()[0])
        network = {}
        try:
            path = self.data / 'network-monitor.json'
            if path.stat().st_size <= 1024 * 1024:
                network = redact(json.loads(path.read_text(encoding='utf-8')))
        except (OSError, ValueError):
            pass
        service = self.service_state()
        return {'service': service, 'protocol': self.protocol(), 'monitor': self.monitor(service), 'network': network, 'metrics': self.metrics(), 'host': {'name': socket.gethostname(), 'uptimeSeconds': int(uptime), 'nodeVersion': self.command('/usr/local/bin/node', '--version', check=False)}}

    def logs(self):
        text = self.command('/usr/bin/journalctl', '-u', self.service, '--no-pager', '-o', 'short-iso', '-n', '200', '--since', '2 hours ago')
        # Remove literal values from configured private JSON files as well as labelled secrets.
        secrets = set()
        def collect(value):
            if isinstance(value, dict):
                for key, child in value.items():
                    if SECRET_KEY.search(str(key)) and isinstance(child, str) and len(child) >= 4:
                        secrets.add(child)
                    else:
                        collect(child)
            elif isinstance(value, list):
                for child in value:
                    collect(child)
        for name in self.private_files:
            try:
                collect(json.loads((self.data / name).read_text(encoding='utf-8')))
            except (OSError, ValueError):
                pass
        for secret in sorted(secrets, key=len, reverse=True):
            text = text.replace(secret, '[masqué]')
        return {'text': redact(text)[-131072:]}

    def ensure_backup_dir(self):
        self.backup_dir.mkdir(parents=True, exist_ok=True, mode=0o700)
        os.chmod(self.backup_dir, 0o700)

    def checked_snapshot(self, name):
        if not isinstance(name, str) or not SNAPSHOT_NAME.fullmatch(name):
            raise ValueError('Invalid snapshot name')
        path = self.backup_dir / name
        if path.is_symlink() or not path.is_file() or path.resolve().parent != self.backup_dir:
            raise ValueError('Snapshot not found')
        return path

    @staticmethod
    def verify_database(path):
        with contextlib.closing(sqlite3.connect(path.as_uri() + '?mode=ro', uri=True, timeout=10)) as database:
            result = database.execute('PRAGMA integrity_check').fetchall()
            if result != [('ok',)]:
                raise ValueError('SQLite integrity check failed')
            return database.execute("SELECT count(*) FROM sqlite_master WHERE type='table'").fetchone()[0]

    def backup(self):
        self.ensure_backup_dir()
        if self.database.is_symlink() or not self.database.is_file():
            raise ValueError('Database not found or unsafe')
        timestamp = dt.datetime.now(dt.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
        name = f'eclat-{timestamp}-{os.urandom(4).hex()}.tar.gz'
        destination = self.backup_dir / name
        with tempfile.TemporaryDirectory(prefix='.snapshot-', dir=self.backup_dir) as temp:
            db_copy = Path(temp) / 'coop.sqlite'
            with contextlib.closing(sqlite3.connect(self.database.as_uri() + '?mode=ro', uri=True, timeout=30)) as source:
                with contextlib.closing(sqlite3.connect(db_copy)) as target:
                    source.backup(target)
            tables = self.verify_database(db_copy)
            temporary = Path(temp) / name
            with tarfile.open(temporary, 'w:gz') as archive:
                archive.add(db_copy, arcname='coop.sqlite', recursive=False)
                included = []
                for entry in self.private_files:
                    source = self.data / entry
                    if source.is_symlink():
                        raise ValueError('Refusing symbolic snapshot configuration')
                    if source.is_file():
                        archive.add(source, arcname='config/' + entry, recursive=False)
                        included.append(entry)
                manifest = json.dumps({'format': 1, 'createdUtc': timestamp, 'database': 'coop.sqlite', 'tables': tables, 'configFiles': included, 'databaseSha256': hashlib.sha256(db_copy.read_bytes()).hexdigest()}).encode()
                info = tarfile.TarInfo('manifest.json')
                info.size = len(manifest)
                info.mode = 0o600
                archive.addfile(info, io.BytesIO(manifest))
            os.chmod(temporary, 0o600)
            os.replace(temporary, destination)
        result = self.backup_info(destination)
        result.update({'sha256': hashlib.sha256(destination.read_bytes()).hexdigest(), 'integrity': 'ok'})
        return result

    @staticmethod
    def backup_info(path):
        stats = path.stat()
        return {'name': path.name, 'size': stats.st_size, 'modifiedUtc': dt.datetime.fromtimestamp(stats.st_mtime, dt.timezone.utc).isoformat()}

    def backups(self):
        self.ensure_backup_dir()
        paths = [path for path in self.backup_dir.iterdir() if SNAPSHOT_NAME.fullmatch(path.name) and path.is_file() and not path.is_symlink()]
        # The suffix is random: names from the same second do not encode creation order.
        return {'items': [self.backup_info(path) for path in sorted(paths, key=lambda item: (item.stat().st_mtime_ns, item.name), reverse=True)]}

    def snapshot_database(self, path):
        with tarfile.open(path, 'r:gz') as archive:
            members = archive.getmembers()
            allowed = {'coop.sqlite', 'manifest.json'} | {'config/' + name for name in self.private_files}
            if len({item.name for item in members}) != len(members) or any(item.name not in allowed or not item.isfile() for item in members):
                raise ValueError('Unsafe snapshot archive')
            member = archive.getmember('coop.sqlite')
            if member.size <= 0 or member.size > MAX_DOWNLOAD:
                raise ValueError('Database exceeds snapshot transfer limit (64 MiB)')
            data = archive.extractfile(member).read(MAX_DOWNLOAD + 1)
            manifest = json.load(archive.extractfile('manifest.json'))
            if manifest.get('databaseSha256') != hashlib.sha256(data).hexdigest():
                raise ValueError('Snapshot checksum mismatch')
            return data

    def download(self, name):
        path = self.checked_snapshot(name)
        database = self.snapshot_database(path)
        return {'name': name[:-7] + '.sqlite', 'base64': base64.b64encode(database).decode('ascii'), 'sha256': hashlib.sha256(database).hexdigest()}

    def set_database_owner(self, path):
        import pwd
        owner = self.config.get('serviceUser', 'eclat-server')
        account = pwd.getpwnam(owner)
        # The game owns its data directory: never follow a path it could replace with a symlink.
        descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
        try:
            if not stat.S_ISREG(os.fstat(descriptor).st_mode):
                raise ValueError('Refusing nonregular database ownership change')
            os.fchown(descriptor, account.pw_uid, account.pw_gid)
            os.fchmod(descriptor, 0o600)
        finally:
            os.close(descriptor)

    def restore(self, name):
        snapshot = self.checked_snapshot(name)
        content = self.snapshot_database(snapshot)
        self.ensure_backup_dir()
        if self.backup_dir.stat().st_dev != self.data.stat().st_dev:
            raise ValueError('Restore staging and game data must use the same filesystem; service unchanged')
        # Validate outside the live database before stopping a working game.
        # Root-private staging cannot be renamed or altered by the running game account.
        with tempfile.NamedTemporaryFile(prefix='.restore-', dir=self.backup_dir, delete=False) as target:
            candidate = Path(target.name)
            target.write(content)
            target.flush()
            os.fsync(target.fileno())
        try:
            self.verify_database(candidate)
            self.set_database_owner(candidate)
            was_active = self.service_state()['active'] in {'active', 'activating', 'reloading'}
            previous = self.data / '.coop.sqlite.rollback'
            if previous.exists() or previous.is_symlink():
                raise ValueError('A previous rollback database requires operator inspection')
            self.ensure_stopped()
            rollback_ready = False
            rollback_created = False
            try:
                # Service and all children are stopped before snapshotting and touching WAL files.
                rollback = self.backup()
                # Store the consolidated SQLite backup for rollback, including any committed WAL pages.
                rollback_content = self.snapshot_database(self.checked_snapshot(rollback['name']))
                with previous.open('xb') as target:
                    rollback_created = True
                    target.write(rollback_content)
                    target.flush()
                    os.fsync(target.fileno())
                self.verify_database(previous)
                self.set_database_owner(previous)
                rollback_ready = True
                for suffix in ('-wal', '-shm'):
                    (Path(str(self.database) + suffix)).unlink(missing_ok=True)
                os.replace(candidate, self.database)
                self.verify_database(self.database)
                if was_active:
                    self.command('/usr/bin/systemctl', 'start', self.service, timeout=45)
                    self.wait_healthy()
                previous.unlink()
                return {'name': name, 'restored': True, 'rollbackBackup': rollback['name'], 'integrity': 'ok'}
            except Exception:
                if rollback_ready:
                    # Never swap database files under a process that might still have SQLite open.
                    self.ensure_stopped()
                    for suffix in ('-wal', '-shm'):
                        (Path(str(self.database) + suffix)).unlink(missing_ok=True)
                    os.replace(previous, self.database)
                    self.set_database_owner(self.database)
                elif rollback_created:
                    previous.unlink(missing_ok=True)
                if was_active:
                    self.command('/usr/bin/systemctl', 'start', self.service, timeout=45)
                    self.wait_healthy()
                raise
        finally:
            candidate.unlink(missing_ok=True)
            for suffix in ('-wal', '-shm'):
                Path(str(candidate) + suffix).unlink(missing_ok=True)

    def ensure_stopped(self):
        self.command('/usr/bin/systemctl', 'stop', self.service, timeout=60)
        state = self.service_state()
        if state['active'] not in {'inactive', 'failed'} or state.get('pid', 0) != 0:
            raise RuntimeError('Service stop could not be confirmed; no database replacement performed')

    def wait_healthy(self):
        for _ in range(15):
            if self.protocol()['ok']:
                return
            time.sleep(1)
        raise RuntimeError('Restored server did not pass its protocol check; previous database restored')

    def dispatch(self, request):
        if not isinstance(request, dict) or set(request) - {'action', 'name'}:
            raise ValueError('Invalid request fields')
        action = request.get('action')
        if not isinstance(action, str) or action not in ACTIONS:
            raise ValueError('Unknown action')
        if action in {'start', 'stop', 'restart'}:
            self.command('/usr/bin/systemctl', action, self.service, timeout=45)
            return {'service': self.service_state()}
        if action in {'download', 'restore'}:
            return getattr(self, action)(request.get('name'))
        if action == 'diagnostics':
            return {'status': self.status(), 'journal': self.logs()['text'], 'backupCount': len(self.backups()['items'])}
        return getattr(self, action)()

    def watchdog(self):
        state_path = Path(self.config.get('watchdogState', '/run/eclat-panel-health.json'))
        # An intentional stop must stay stopped.
        active = self.service_state()['active'] == 'active'
        if not active or self.protocol()['ok']:
            state_path.unlink(missing_ok=True)
            return
        try:
            previous = json.loads(state_path.read_text())
            failures = int(previous.get('failures', 0)) + 1
        except (OSError, ValueError, TypeError):
            failures = 1
        state_path.write_text(json.dumps({'failures': failures}), encoding='utf-8')
        os.chmod(state_path, 0o600)
        if failures >= 3:
            if self.service_state()['active'] != 'active':
                state_path.unlink(missing_ok=True)
                return
            self.command('/usr/bin/logger', '-t', 'eclat-watchdog', 'Game protocol failed three times; restarting service')
            self.command('/usr/bin/systemctl', 'restart', self.service, timeout=45)
            state_path.unlink(missing_ok=True)


def main():
    os.umask(0o077)
    try:
        stat = CONFIG_PATH.stat()
        if stat.st_uid != 0 or stat.st_mode & 0o022:
            raise ValueError('Unsafe admin configuration permissions')
        config = json.loads(CONFIG_PATH.read_text(encoding='utf-8'))
        admin = Admin(config)
        if sys.argv[1:] == ['--watchdog']:
            with operation_lock():
                admin.watchdog()
            return
        if sys.argv[1:] == ['--backup']:
            with operation_lock():
                result = admin.backup()
            print(json.dumps({'ok': True, 'data': result}, ensure_ascii=False))
            return
        if len(sys.argv) != 1:
            raise ValueError('Arguments are not accepted')
        raw = sys.stdin.buffer.read(MAX_REQUEST + 1)
        if len(raw) > MAX_REQUEST:
            raise ValueError('Request too large')
        request = json.loads(raw)
        if isinstance(request, dict) and request.get('action') in {'start', 'stop', 'restart', 'backup', 'restore'}:
            with operation_lock():
                result = admin.dispatch(request)
        else:
            result = admin.dispatch(request)
        print(json.dumps({'ok': True, 'data': result}, ensure_ascii=False))
    except Exception as exc:
        print(json.dumps({'ok': False, 'error': redact(str(exc))}, ensure_ascii=False))
        sys.exit(1)


@contextlib.contextmanager
def operation_lock():
    """Serialize service changes and SQLite restore against other admins and timers."""
    import fcntl
    with open('/run/lock/eclat-panel.lock', 'a', encoding='ascii') as handle:
        os.chmod('/run/lock/eclat-panel.lock', 0o600)
        try:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RuntimeError('Another administration operation is in progress; retry shortly') from None
        try:
            yield
        finally:
            fcntl.flock(handle, fcntl.LOCK_UN)


if __name__ == '__main__':
    main()
