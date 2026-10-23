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
import uuid

CONFIG_PATH = Path('/etc/eclat-panel.json')
MAX_REQUEST = 96 * 1024
MAX_DOWNLOAD = 64 * 1024 * 1024
MAX_CONFIG = 64 * 1024
MAX_ADMIN_RESPONSE = 1024 * 1024
ADMIN_DEADLINE = 35
ACTIONS = {'close-room', 'status', 'logs', 'start', 'stop', 'restart', 'backup', 'backups', 'download', 'restore', 'diagnostics',
           'admin.overview', 'admin.audit', 'admin.command', 'config.get', 'config.save', 'config.history', 'config.restore', 'statistics'}
MUTATIONS = {'start', 'stop', 'restart', 'backup', 'restore', 'close-room', 'admin.command', 'config.save', 'config.restore'}
LEGACY_NO_NAME = {'status', 'logs', 'start', 'stop', 'restart', 'backup', 'backups', 'diagnostics'}
ADMIN_COMMANDS = {'player.kick', 'account.ban', 'account.unban', 'room.close', 'room.update', 'maintenance.set', 'eclats.adjust', 'casino.refund'}
SNAPSHOT_NAME = re.compile(r'^eclat-\d{8}T\d{6}Z-[a-f0-9]{8}\.tar\.gz$')
CONFIG_VERSION = re.compile(r'^eclats-\d{8}T\d{12}Z-[a-f0-9]{8}\.json$')
SHA256 = re.compile(r'^[a-f0-9]{64}$')
UUID = re.compile(r'^[a-f0-9]{8}-[a-f0-9]{4}-[1-8][a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$', re.I)
SECRET_KEY = re.compile(r'(?:password|passwd|secret|token|authorization|credential|private.?key|publish.?key|api.?key|auth.?key|session.?key|account.?key)', re.I)


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
        value = re.sub(r'\b[A-Za-z0-9_-]{43}\b', '[clé de compte masquée]', value)
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
        self.socket_path = Path(config.get('adminSocket', str(self.data / 'admin.sock')))
        if not self.socket_path.is_absolute():
            raise ValueError('Invalid administration socket path')
        self.config_path = self.data / 'eclats.json'
        self.history_dir = self.backup_dir / 'configuration'
        self.audit_path = self.backup_dir / 'configuration-audit.jsonl'
        self.lock_path = Path(config.get('operationLock', '/run/lock/eclat-panel.lock'))
        if not self.lock_path.is_absolute():
            raise ValueError('Invalid operation lock path')
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
                probe = self.probe_connection(connection)
            return {'ok': True, 'maintenance': bool(probe.get('maintenance')), 'latencyMs': round((time.monotonic() - started) * 1000, 1), 'error': None}
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
            if not isinstance(message, dict):
                raise RuntimeError('Invalid game response')
            if (expected == 'hello.ok' and message.get('type') == 'error'
                    and isinstance(message.get('payload'), dict) and message['payload'].get('code') == 'MAINTENANCE'):
                # A deliberate admission refusal proves the game is running.
                # The watchdog must preserve the ongoing parties during maintenance.
                return {'maintenance': True}
            if message.get('type') != expected:
                raise RuntimeError(f'Unexpected game reply: {message.get("type", "missing type")}')
        return {'maintenance': False}

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

    @staticmethod
    def checked_uuid(value):
        if not isinstance(value, str) or not UUID.fullmatch(value):
            raise ValueError('A UUID requestId is required')
        try:
            parsed = uuid.UUID(value)
        except (ValueError, AttributeError):
            raise ValueError('A UUID requestId is required') from None
        if str(parsed) != value.lower():
            raise ValueError('Invalid requestId')
        return str(parsed)

    @staticmethod
    def checked_reason(value):
        if not isinstance(value, str) or value != value.strip() or not 3 <= len(value) <= 240 or any(ord(char) < 32 or ord(char) == 127 for char in value):
            raise ValueError('A reason of 3 to 240 characters without surrounding spaces is required')
        return value

    @staticmethod
    def checked_identifier(value, kind):
        if not isinstance(value, str) or not UUID.fullmatch(value):
            raise ValueError(f'Invalid {kind}')
        return value

    def checked_admin_arguments(self, command, arguments):
        if command not in ADMIN_COMMANDS or not isinstance(arguments, dict):
            raise ValueError('Unknown administration command or invalid arguments')
        fields = {
            'player.kick': ({'playerId'}, set()), 'account.ban': ({'accountHash', 'reason', 'expiresAt'}, set()),
            'account.unban': ({'accountHash'}, {'reason'}), 'room.close': ({'roomId'}, set()),
            'room.update': ({'roomId'}, {'capacity', 'visibility'}), 'maintenance.set': ({'enabled'}, set()),
            'eclats.adjust': ({'accountHash', 'amount', 'reason'}, set()),
            'casino.refund': ({'accountHash', 'amount', 'reason'}, set()),
        }
        required, optional = fields[command]
        if not required <= arguments.keys() or arguments.keys() - required - optional:
            raise ValueError('Invalid administration command fields')
        result = dict(arguments)
        for key in ('playerId', 'roomId'):
            if key in result:
                self.checked_identifier(result[key], key)
        if 'accountHash' in result and (not isinstance(result['accountHash'], str) or not SHA256.fullmatch(result['accountHash'])):
            raise ValueError('Invalid account fingerprint; account keys are never accepted')
        if 'reason' in result:
            result['reason'] = self.checked_reason(result['reason'])
        if command == 'account.ban' and result['expiresAt'] is not None:
            value = result['expiresAt']
            try:
                expires = dt.datetime.fromisoformat(value.replace('Z', '+00:00')) if isinstance(value, str) and re.fullmatch(r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,3})?Z', value) else None
            except ValueError:
                expires = None
            now = dt.datetime.now(dt.timezone.utc)
            if expires is None or expires.tzinfo is None or expires <= now or expires > now + dt.timedelta(days=10 * 366):
                raise ValueError('Ban expiry must be a future ISO UTC date within ten years, or null')
        if command == 'room.update':
            if not ({'capacity', 'visibility'} & result.keys()):
                raise ValueError('A capacity or visibility change is required')
            if 'capacity' in result and (type(result['capacity']) is not int or not 2 <= result['capacity'] <= 10):
                raise ValueError('Room capacity must be between 2 and 10')
            if 'visibility' in result and result['visibility'] not in ('public', 'private'):
                raise ValueError('Invalid room visibility')
        if command == 'maintenance.set' and type(result['enabled']) is not bool:
            raise ValueError('Maintenance enabled must be a boolean')
        if command in ('eclats.adjust', 'casino.refund'):
            amount = result['amount']
            if type(amount) is not int or (command == 'eclats.adjust' and (amount == 0 or abs(amount) > 2147483647)) or (command == 'casino.refund' and not 1 <= amount <= 9999):
                raise ValueError('Invalid administration amount')
        return result

    def admin_exchange(self, command, arguments, request_id=None):
        """One local command, one reply, no network listener and no mutation retry."""
        if command not in ADMIN_COMMANDS | {'admin.overview', 'admin.audit'}:
            raise ValueError('Unknown local administration command')
        request_id = self.checked_uuid(request_id or str(uuid.uuid4()))
        raw = json.dumps({'version': 1, 'requestId': request_id, 'command': command, 'arguments': arguments}, ensure_ascii=False).encode('utf-8') + b'\n'
        if len(raw) > MAX_REQUEST:
            raise ValueError('Administration command too large')
        try:
            info = self.socket_path.lstat()
            if not stat.S_ISSOCK(info.st_mode) or info.st_mode & 0o077:
                raise ValueError('Unsafe local administration socket; expected a private 0600 socket')
            import pwd
            owner = pwd.getpwnam(self.config.get('serviceUser', 'eclat-server'))
            if info.st_uid != owner.pw_uid:
                raise ValueError('Administration socket is not owned by the game service')
        except FileNotFoundError:
            raise RuntimeError('Administration unavailable: install a compatible game server and start its local socket') from None
        deadline = time.monotonic() + ADMIN_DEADLINE
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
                connection.settimeout(max(0.01, deadline - time.monotonic()))
                connection.connect(str(self.socket_path))
                if hasattr(socket, 'SO_PEERCRED'):
                    import struct
                    _, peer_uid, _ = struct.unpack('3i', connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize('3i')))
                    if peer_uid != owner.pw_uid:
                        raise ValueError('Administration socket peer is not the game service')
                connection.sendall(raw)
                response = bytearray()
                while b'\n' not in response:
                    connection.settimeout(max(0.01, deadline - time.monotonic()))
                    block = connection.recv(min(65536, MAX_ADMIN_RESPONSE + 1 - len(response)))
                    if not block:
                        raise RuntimeError('Local administration closed without a reply; command result unknown')
                    response.extend(block)
                    if len(response) > MAX_ADMIN_RESPONSE:
                        raise RuntimeError('Local administration reply exceeds 1 MiB')
                line, remainder = bytes(response).split(b'\n', 1)
                if remainder.strip():
                    raise RuntimeError('Invalid local administration framing')
                envelope = json.loads(line.decode('utf-8'))
        except (TimeoutError, socket.timeout):
            raise RuntimeError('Local administration timed out; command result unknown; inspect the audit before retrying') from None
        except (ConnectionError, OSError):
            raise RuntimeError('Local administration is unavailable; command result unknown') from None
        except (UnicodeError, json.JSONDecodeError):
            raise RuntimeError('Invalid local administration JSON response') from None
        if not isinstance(envelope, dict) or type(envelope.get('ok')) is not bool:
            raise RuntimeError('Invalid local administration response')
        if envelope['ok'] is False:
            error = envelope.get('error')
            if not isinstance(error, dict) or not isinstance(error.get('code'), str) or not isinstance(error.get('message'), str):
                raise RuntimeError('Invalid local administration error')
            raise RuntimeError(f'{redact(error["code"][:64])}: {redact(error["message"][:500])}')
        if not isinstance(envelope.get('data'), dict):
            raise RuntimeError('Invalid local administration data')
        return redact(envelope['data'])

    def admin_overview(self):
        result = self.admin_exchange('admin.overview', {})
        result['capabilities'] = {'commands': True, 'configuration': True, 'history': True, 'statistics': True}
        return result

    def admin_audit(self):
        remote_error = None
        try:
            items = self.admin_exchange('admin.audit', {'limit': 200}).get('items', [])
            if not isinstance(items, list):
                raise RuntimeError('Invalid administration audit list')
        except RuntimeError as error:
            items = []
            remote_error = str(error)
        local = {}
        for item in self.config_audit_records():
            local[item['requestId']] = item
        items.extend(local.values())
        result = {'items': sorted(items, key=lambda item: str(item.get('at', '')), reverse=True)[:200]}
        if remote_error:
            result['serverAuditUnavailable'] = remote_error
        return result

    @staticmethod
    def read_regular(path, limit):
        descriptor = os.open(path, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0))
        try:
            info = os.fstat(descriptor)
            if not stat.S_ISREG(info.st_mode) or info.st_size > limit:
                raise ValueError('Unsafe or oversized configuration file')
            with os.fdopen(descriptor, 'rb', closefd=False) as source:
                raw = source.read(limit + 1)
            if len(raw) > limit:
                raise ValueError('Configuration file too large')
            return raw, info
        finally:
            os.close(descriptor)

    def current_config_bytes(self):
        try:
            return self.read_regular(self.config_path, MAX_CONFIG)
        except FileNotFoundError:
            return b'', None

    def config_status(self, expected=None):
        snapshot = self.monitor()
        state = snapshot.get('eclatsConfig')
        try:
            updated = dt.datetime.fromisoformat(str(snapshot.get('updatedAt', '')).replace('Z', '+00:00'))
            fresh = updated.tzinfo is not None and -10 <= (dt.datetime.now(dt.timezone.utc) - updated).total_seconds() <= 15
        except ValueError:
            fresh = False
        if not isinstance(state, dict) or snapshot.get('processCurrent') is not True or not fresh:
            return {'state': 'pending', 'sha256': expected, 'message': 'Acceptance by the running game has not been confirmed'}
        sha = state.get('sha256')
        if expected is not None and sha != expected:
            return {'state': 'pending', 'sha256': expected, 'loadedSha256': state.get('loadedSha256'), 'message': 'Waiting for the game to reload this version'}
        if state.get('state') not in ('accepted', 'rejected') or not isinstance(sha, (str, type(None))) or (isinstance(sha, str) and not SHA256.fullmatch(sha)):
            return {'state': 'pending', 'sha256': expected, 'message': 'The game did not publish a valid configuration acknowledgement'}
        if state['state'] == 'accepted' and state.get('loadedSha256') != sha:
            return {'state': 'pending', 'sha256': expected, 'message': 'Loaded configuration checksum does not match the acknowledgement'}
        return {key: state[key] for key in ('state', 'sha256', 'loadedSha256', 'updatedAt') if key in state}

    def config_get(self):
        raw, info = self.current_config_bytes()
        invalid = False
        if info is not None:
            try:
                value = json.loads(raw)
                if not isinstance(value, dict):
                    invalid = True
                    value = None
            except (ValueError, UnicodeError):
                invalid = True
                value = None
        else:
            value = self.run_config_validator('--default').get('config')
            if not isinstance(value, dict):
                raise ValueError('The game did not provide a valid default configuration')
        checksum = hashlib.sha256(raw).hexdigest()
        result = {'config': value, 'sha256': checksum, 'exists': info is not None, 'status': self.config_status(checksum),
                  'capabilities': {'configuration': True, 'history': True, 'statistics': True}}
        if invalid:
            recovery = self.run_config_validator('--default').get('config')
            if not isinstance(recovery, dict):
                raise ValueError('The game did not provide a valid recovery configuration')
            result.update({'fileInvalid': True, 'recoveryConfig': recovery, 'raw': raw.decode('utf-8', errors='replace')})
            if result['status']['state'] == 'accepted':
                result['status'] = {'state': 'pending', 'sha256': checksum, 'message': 'Invalid JSON file; game acceptance cannot be confirmed'}
        return result

    def run_config_validator(self, argument):
        validator = Path(self.config.get('configValidator', '/opt/eclat-originel/app/server/src/admin-validate-config.js'))
        node = Path(self.config.get('nodeBinary', '/usr/local/bin/node'))
        if not validator.is_absolute() or not node.is_absolute() or not validator.is_file():
            raise RuntimeError('Configuration unavailable: the compatible game configuration validator must be installed')
        response = subprocess.run([str(node), str(validator), str(argument)], capture_output=True, timeout=15, check=False)
        if len(response.stdout) > 65536:
            raise ValueError('Invalid configuration validator response')
        try:
            envelope = json.loads(response.stdout)
        except (ValueError, UnicodeError):
            raise RuntimeError('The game configuration validator did not return a valid result') from None
        if response.returncode != 0 or not isinstance(envelope, dict) or envelope.get('ok') is not True:
            raise ValueError('Configuration rejected by the game validator (INVALID_ECLATS_CONFIG)')
        return envelope

    def validate_config(self, content):
        if len(content) > MAX_CONFIG:
            raise ValueError('Configuration exceeds 64 KiB')
        self.ensure_backup_dir()
        with tempfile.NamedTemporaryFile(prefix='.validate-config-', dir=self.backup_dir, delete=False) as temporary:
            path = Path(temporary.name)
            temporary.write(content)
        try:
            envelope = self.run_config_validator(path)
            if envelope.get('sha256') != hashlib.sha256(content).hexdigest():
                raise ValueError('Game configuration validator checksum mismatch')
        finally:
            path.unlink(missing_ok=True)

    @staticmethod
    def atomic_write(path, content, owner=None):
        descriptor, filename = tempfile.mkstemp(prefix='.' + path.name + '-', dir=path.parent)
        temporary = Path(filename)
        try:
            os.fchmod(descriptor, stat.S_IMODE(owner.st_mode) if owner else 0o600)
            if owner is not None and hasattr(os, 'fchown'):
                os.fchown(descriptor, owner.st_uid, owner.st_gid)
            with os.fdopen(descriptor, 'wb', closefd=False) as target:
                target.write(content)
                target.flush()
                os.fsync(descriptor)
            os.replace(temporary, path)
            if hasattr(os, 'O_DIRECTORY'):
                directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    os.fsync(directory)
                finally:
                    os.close(directory)
        finally:
            os.close(descriptor)
            temporary.unlink(missing_ok=True)

    def ensure_history_dir(self):
        self.ensure_backup_dir()
        if self.history_dir.is_symlink():
            raise ValueError('Unsafe configuration history directory')
        self.history_dir.mkdir(mode=0o700, exist_ok=True)
        os.chmod(self.history_dir, 0o700)

    def record_config_version(self, content, reason, request_id):
        if len(content) > MAX_CONFIG:
            raise ValueError('Configuration exceeds 64 KiB')
        self.ensure_history_dir()
        now = dt.datetime.now(dt.timezone.utc)
        version = f'eclats-{now.strftime("%Y%m%dT%H%M%S%fZ")}-{os.urandom(4).hex()}.json'
        item = {'id': version, 'modifiedUtc': now.isoformat(), 'sha256': hashlib.sha256(content).hexdigest(),
                'reason': reason, 'requestId': request_id}
        try:
            item['content'] = content.decode('utf-8')
        except UnicodeError:
            # Preserve a broken external version as bytes while allowing its repair.
            item['contentBase64'] = base64.b64encode(content).decode('ascii')
        self.atomic_write(self.history_dir / version, json.dumps(item, ensure_ascii=False).encode('utf-8'))
        paths = sorted((path for path in self.history_dir.iterdir() if CONFIG_VERSION.fullmatch(path.name) and not path.is_symlink()), key=lambda path: path.name, reverse=True)
        for old in paths[20:]:
            old.unlink()
        return version

    def checked_config_version(self, version):
        if not isinstance(version, str) or not CONFIG_VERSION.fullmatch(version):
            raise ValueError('Invalid configuration version')
        raw, _ = self.read_regular(self.history_dir / version, MAX_CONFIG * 4 + 4096)
        item = json.loads(raw)
        if not isinstance(item, dict) or item.get('id') != version:
            raise ValueError('Invalid configuration history record')
        if isinstance(item.get('content'), str) and 'contentBase64' not in item:
            content = item['content'].encode('utf-8')
        elif isinstance(item.get('contentBase64'), str) and 'content' not in item:
            try:
                content = base64.b64decode(item['contentBase64'], validate=True)
            except ValueError:
                raise ValueError('Invalid configuration history bytes') from None
        else:
            raise ValueError('Invalid configuration history record')
        if len(content) > MAX_CONFIG or item.get('sha256') != hashlib.sha256(content).hexdigest():
            raise ValueError('Configuration history checksum mismatch')
        return item, content

    def config_history(self):
        self.ensure_history_dir()
        items = []
        for path in sorted(self.history_dir.iterdir(), key=lambda item: item.name, reverse=True):
            if not CONFIG_VERSION.fullmatch(path.name):
                continue
            item, _ = self.checked_config_version(path.name)
            items.append({key: item[key] for key in ('id', 'modifiedUtc', 'sha256', 'reason')})
            if len(items) == 20:
                break
        return {'items': items}

    def config_audit_records(self):
        try:
            raw, _ = self.read_regular(self.audit_path, 8 * 1024 * 1024)
        except FileNotFoundError:
            return []
        records = []
        for line in raw.splitlines()[-400:]:
            try:
                value = json.loads(line)
                if isinstance(value, dict) and isinstance(value.get('requestId'), str):
                    records.append(redact(value))
            except (ValueError, UnicodeError):
                continue
        return records

    def config_audit(self, request_id, command, reason, result):
        self.ensure_backup_dir()
        line = json.dumps({'at': dt.datetime.now(dt.timezone.utc).isoformat(), 'requestId': request_id, 'command': command,
                           'target': 'eclats.json', 'reason': redact(reason), 'result': result}, ensure_ascii=False).encode('utf-8') + b'\n'
        descriptor = os.open(self.audit_path, os.O_WRONLY | os.O_CREAT | os.O_APPEND | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0), 0o600)
        try:
            if not stat.S_ISREG(os.fstat(descriptor).st_mode):
                raise ValueError('Unsafe configuration audit file')
            os.fchmod(descriptor, 0o600)
            if os.fstat(descriptor).st_size > 4 * 1024 * 1024:
                recent = self.config_audit_records()[-400:]
                self.atomic_write(self.audit_path, b''.join(json.dumps(item, ensure_ascii=False).encode('utf-8') + b'\n' for item in recent))
                os.close(descriptor)
                descriptor = os.open(self.audit_path, os.O_WRONLY | os.O_APPEND | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0))
            with os.fdopen(descriptor, 'ab', closefd=False) as target:
                target.write(line)
                target.flush()
                os.fsync(descriptor)
        finally:
            os.close(descriptor)

    def change_config(self, request):
        request_id = self.checked_uuid(request.get('requestId'))
        reason = self.checked_reason(request.get('reason'))
        expected = request.get('expectedSha256')
        if not isinstance(expected, str) or not SHA256.fullmatch(expected):
            raise ValueError('expectedSha256 is required to protect concurrent edits')
        if any(item['requestId'] == request_id for item in self.config_audit_records()):
            raise ValueError('Configuration requestId already used; inspect history before retrying')
        action = request['action']
        self.config_audit(request_id, action, reason, {'ok': False, 'code': 'PENDING'})
        result_code = 'CONFIG_INVALID'
        wrote = False
        try:
            current, owner = self.current_config_bytes()
            if hashlib.sha256(current).hexdigest() != expected:
                result_code = 'CONFIG_CONFLICT'
                raise ValueError('CONFIG_CONFLICT: configuration changed since it was loaded; reload before saving')
            if action == 'config.restore':
                _, candidate = self.checked_config_version(request.get('version'))
            else:
                value = request.get('config')
                if not isinstance(value, dict):
                    raise ValueError('Configuration must be a JSON object')
                candidate = (json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + '\n').encode('utf-8')
            self.validate_config(candidate)
            if current and not self.config_history()['items']:
                self.record_config_version(current, 'Version before the first administration change', str(uuid.uuid4()))
            latest, latest_owner = self.current_config_bytes()
            if hashlib.sha256(latest).hexdigest() != expected:
                result_code = 'CONFIG_CONFLICT'
                raise ValueError('CONFIG_CONFLICT: configuration changed during validation')
            if owner is None:
                import pwd
                account = pwd.getpwnam(self.config.get('serviceUser', 'eclat-server'))
                class InitialOwner:
                    st_mode = 0o600
                    st_uid = account.pw_uid
                    st_gid = account.pw_gid
                latest_owner = InitialOwner()
            self.atomic_write(self.config_path, candidate, latest_owner)
            wrote = True
            version = self.record_config_version(candidate, reason, request_id)
            checksum = hashlib.sha256(candidate).hexdigest()
            deadline = time.monotonic() + 8
            status = self.config_status(checksum)
            while status['state'] == 'pending' and time.monotonic() < deadline:
                time.sleep(0.25)
                status = self.config_status(checksum)
            accepted = status['state'] == 'accepted' and status.get('loadedSha256') == checksum
            result_code = 'ACCEPTED' if accepted else 'REJECTED' if status['state'] == 'rejected' else 'PENDING_RELOAD'
            return {'sha256': checksum, 'accepted': accepted, 'status': status, 'version': version}
        except Exception:
            if wrote:
                result_code = 'RESULT_UNKNOWN'
            raise
        finally:
            self.config_audit(request_id, action, reason, {'ok': result_code == 'ACCEPTED', 'code': result_code, 'written': wrote})

    def statistics(self):
        if self.database.is_symlink() or not self.database.is_file():
            raise ValueError('Database not found or unsafe')
        first_day = dt.datetime.now(dt.timezone.utc).date() - dt.timedelta(days=29)
        start = int(dt.datetime.combine(first_day, dt.time.min, dt.timezone.utc).timestamp() * 1000)
        with contextlib.closing(sqlite3.connect(self.database.as_uri() + '?mode=ro', uri=True, timeout=5)) as database:
            database.execute('PRAGMA query_only=ON')
            database.row_factory = sqlite3.Row
            database.create_function('eclat_is_shiny', 2, lambda pid, ot: int(((int(pid) & 65535) ^ (int(pid) >> 16) ^ (int(ot) & 65535) ^ (int(ot) >> 16)) < 8), deterministic=True)
            deadline = time.monotonic() + 10
            database.set_progress_handler(lambda: int(time.monotonic() > deadline), 10000)
            database.execute('BEGIN')
            try:
                totals = dict(database.execute('SELECT (SELECT COUNT(*) FROM eclats_accounts) AS accounts, (SELECT COALESCE(SUM(delta),0) FROM eclats_ledger) AS eclatsInCirculation, (SELECT COUNT(*) FROM eclats_eggs) AS eggs, (SELECT COUNT(*) FROM eclats_pulls) AS pulls').fetchone())
                draws = [dict(row) for row in database.execute('SELECT e.banner,COUNT(*) AS eggs,COUNT(DISTINCT e.pull_id) AS pulls,SUM(eclat_is_shiny(e.pid,a.ot_id)) AS shiny FROM eclats_eggs e JOIN eclats_accounts a ON a.hash=e.hash GROUP BY e.banner ORDER BY e.banner LIMIT 256')]
                casino = [dict(row) for row in database.execute("SELECT game,currency,COUNT(*) AS rounds,SUM(CASE WHEN json_valid(detail) THEN CASE WHEN json_extract(detail,'$.freeSpin')=1 THEN 0 ELSE bet END ELSE bet END) AS wagers,SUM(payout) AS payouts FROM casino_games WHERE CASE WHEN json_valid(detail) THEN COALESCE(json_extract(detail,'$.adminRefund'),0) ELSE 0 END<>1 GROUP BY game,currency ORDER BY game,currency LIMIT 16")]
                refunds = dict(database.execute("SELECT COUNT(*) AS refunds,COALESCE(SUM(payout),0) AS coins FROM casino_games WHERE CASE WHEN json_valid(detail) THEN json_extract(detail,'$.adminRefund') ELSE 0 END=1").fetchone())
                has_activity = database.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='admin_account_activity'").fetchone() is not None
                if has_activity:
                    since = database.execute('SELECT MIN(day) FROM admin_account_activity').fetchone()[0]
                    active = [dict(row) for row in database.execute('SELECT day,COUNT(DISTINCT hash) AS players FROM admin_account_activity WHERE day>=? GROUP BY day ORDER BY day LIMIT 30', (first_day.isoformat(),))]
                else:
                    since = None
                    active = [dict(row) for row in database.execute("SELECT strftime('%Y-%m-%d',at/1000,'unixepoch') AS day,COUNT(DISTINCT hash) AS players FROM (SELECT hash,at FROM eclats_operations WHERE at>=? UNION ALL SELECT hash,at FROM eclats_receipts WHERE at>=? UNION ALL SELECT hash,at FROM eclats_pulls WHERE at>=? UNION ALL SELECT hash,at FROM casino_games WHERE at>=? AND CASE WHEN json_valid(detail) THEN COALESCE(json_extract(detail,'$.adminRefund'),0) ELSE 0 END<>1) GROUP BY day ORDER BY day LIMIT 30", (start, start, start, start))]
                totals['shiny'] = sum(row['shiny'] for row in draws)
                return {'totals': totals, 'drawsByBanner': draws, 'casinoByGame': casino, 'activePlayersByDay': active, 'refundTotals': refunds,
                        'periodDays': 30, 'activityScope': 'authenticatedConnections' if has_activity else 'economy', 'activityAvailableSince': since, 'dayTimezone': 'UTC'}
            except sqlite3.OperationalError as error:
                if 'no such table' in str(error):
                    raise RuntimeError('Statistics unavailable: the game database does not contain the expected economy tables') from None
                raise RuntimeError('Statistics unavailable or query deadline exceeded; no data was changed') from None
            finally:
                database.execute('ROLLBACK')

    def close_room(self, name):
        self.checked_identifier(name, 'room id')
        # Retain Claude's existing room action while using the versioned local channel.
        reply = self.admin_exchange('room.close', {'roomId': name})
        if reply.get('roomId') != name or reply.get('closed') is not True:
            raise RuntimeError('Room closure not confirmed')
        return {'closed': True, 'roomId': name}

    def dispatch(self, request):
        if not isinstance(request, dict):
            raise ValueError('Invalid request fields')
        action = request.get('action')
        if not isinstance(action, str) or action not in ACTIONS:
            raise ValueError('Unknown action')
        fields = {'download': {'name'}, 'restore': {'name'}, 'close-room': {'name'}, 'admin.command': {'requestId', 'command', 'arguments'},
                  'config.save': {'requestId', 'config', 'expectedSha256', 'reason'}, 'config.restore': {'requestId', 'version', 'expectedSha256', 'reason'}}
        allowed = {'action'} | fields.get(action, set())
        # Versions through 1.1.1 always serialize the optional name, even for reads.
        if action in LEGACY_NO_NAME and request.get('name') is None:
            allowed.add('name')
        if set(request) - allowed:
            raise ValueError('Invalid request fields')
        if action in MUTATIONS:
            with operation_lock(self.lock_path):
                return self.dispatch_action(request)
        return self.dispatch_action(request)

    def dispatch_action(self, request):
        action = request['action']
        if action in {'start', 'stop', 'restart'}:
            self.command('/usr/bin/systemctl', action, self.service, timeout=45)
            return {'service': self.service_state()}
        if action == 'close-room':
            return self.close_room(request.get('name'))
        if action in {'download', 'restore'}:
            return getattr(self, action)(request.get('name'))
        if action == 'diagnostics':
            return {'status': self.status(), 'journal': self.logs()['text'], 'backupCount': len(self.backups()['items'])}
        if action == 'admin.command':
            command = request.get('command')
            if not isinstance(command, str):
                raise ValueError('Unknown administration command')
            arguments = self.checked_admin_arguments(command, request.get('arguments'))
            return self.admin_exchange(command, arguments, self.checked_uuid(request.get('requestId')))
        if action == 'admin.overview':
            return self.admin_overview()
        if action == 'admin.audit':
            return self.admin_audit()
        if action == 'config.get':
            return self.config_get()
        if action == 'config.history':
            return self.config_history()
        if action in {'config.save', 'config.restore'}:
            return self.change_config(request)
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
            with operation_lock(admin.lock_path):
                admin.watchdog()
            return
        if sys.argv[1:] == ['--backup']:
            with operation_lock(admin.lock_path):
                result = admin.backup()
            print(json.dumps({'ok': True, 'data': result}, ensure_ascii=False))
            return
        if len(sys.argv) != 1:
            raise ValueError('Arguments are not accepted')
        raw = sys.stdin.buffer.read(MAX_REQUEST + 1)
        if len(raw) > MAX_REQUEST:
            raise ValueError('Request too large')
        request = json.loads(raw)
        result = admin.dispatch(request)
        print(json.dumps({'ok': True, 'data': result}, ensure_ascii=False))
    except Exception as exc:
        print(json.dumps({'ok': False, 'error': redact(str(exc))}, ensure_ascii=False))
        sys.exit(1)


@contextlib.contextmanager
def operation_lock(path=Path('/run/lock/eclat-panel.lock')):
    """Serialize service changes and SQLite restore against other admins and timers."""
    import fcntl
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0), 0o600)
    with os.fdopen(descriptor, 'a', encoding='ascii') as handle:
        if not stat.S_ISREG(os.fstat(handle.fileno()).st_mode):
            raise ValueError('Unsafe administration operation lock')
        os.fchmod(handle.fileno(), 0o600)
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
