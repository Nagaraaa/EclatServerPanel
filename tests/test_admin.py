"""Run with: python -m unittest discover -s tests -v"""
import base64
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sqlite3
import tarfile
import tempfile
import unittest
from unittest.mock import mock_open, patch

SPEC = importlib.util.spec_from_file_location('admin', Path(__file__).resolve().parents[1] / 'server/eclat-admin.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


@contextlib.contextmanager
def database(path):
    connection = sqlite3.connect(path)
    try:
        with connection:
            yield connection
    finally:
        connection.close()


class TestAdmin(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.data = self.root / 'data'
        self.data.mkdir()
        self.backups = self.root / 'backups'
        self.admin = MODULE.Admin({'dataDir': str(self.data), 'backupDir': str(self.backups), 'watchdogState': str(self.root / 'watchdog.json')})
        self.db = self.data / 'coop.sqlite'
        with database(self.db) as connection:
            connection.execute('CREATE TABLE players (id INTEGER PRIMARY KEY, name TEXT, eclats INTEGER)')
            connection.executemany('INSERT INTO players(name,eclats) VALUES (?,?)', [('Alice', 123), ('Benoît', 456)])
        (self.data / 'coop.sqlite.identity.json').write_text(json.dumps({'serverId': 'test-server'}))
        (self.data / 'coop-publish.json').write_text(json.dumps({'publishKey': 'private-test-secret'}))
        (self.data / 'server.json').write_text(json.dumps({'name': 'Test game'}))
        self.admin.set_database_owner = lambda path: None

    def rows(self):
        with database(self.db) as connection:
            return connection.execute('SELECT name,eclats FROM players ORDER BY id').fetchall()

    def test_service_state_includes_current_control_group(self):
        with patch.object(self.admin, 'command', return_value='ActiveState=active\nSubState=running\nMainPID=23846\nControlGroup=/system.slice/eclat-server.service') as command:
            state = self.admin.service_state()
        self.assertEqual(state, {'active': 'active', 'sub': 'running', 'pid': 23846, 'controlGroup': '/system.slice/eclat-server.service'})
        self.assertIn('ControlGroup', command.call_args.args[3])

    def test_monitor_accepts_live_node_child_of_supervisor_in_service_group(self):
        service = {'active': 'active', 'pid': 23846, 'controlGroup': '/system.slice/eclat-server.service'}
        snapshot = {'pid': 23871, 'status': 'running', 'players': [{'name': 'Alice'}]}
        source = self.data / 'monitor.json'
        source.write_text(json.dumps(snapshot))
        read_text = Path.read_text
        for membership in ('0::/system.slice/eclat-server.service\n',
                           '0::/system.slice/eclat-server.service/worker\n',
                           '4:memory:/other\n1:name=systemd:/system.slice/eclat-server.service\n'):
            with self.subTest(membership=membership):
                def read_fixture(path, *args, **kwargs):
                    return membership if path == Path('/proc/23871/cgroup') else read_text(path, *args, **kwargs)
                with patch.object(Path, 'read_text', read_fixture), patch.object(self.admin, 'service_state') as query:
                    result = self.admin.monitor(service)
                self.assertIs(result.pop('processCurrent'), True)
                self.assertEqual(result, snapshot)
                query.assert_not_called()

    def test_monitor_rejects_invalid_pids_without_reading_proc(self):
        service = {'active': 'active', 'controlGroup': '/system.slice/eclat-server.service'}
        for pid in (None, 0, -1, True, 1.5, '23871', [], {}):
            with self.subTest(pid=pid), patch.object(Path, 'read_text') as read:
                self.assertIs(self.admin.monitor_process_current({'pid': pid}, service), False)
                read.assert_not_called()

    def test_monitor_rejects_other_groups_and_prefix_collisions(self):
        service = {'active': 'active', 'controlGroup': '/system.slice/eclat-server.service'}
        for membership in ('0::/system.slice/other.service\n',
                           '0::/system.slice/eclat-server.service-old\n',
                           '4:memory:/system.slice/eclat-server.service\n1:name=systemd:/other\n',
                           'not a cgroup record\n', ''):
            with self.subTest(membership=membership), patch.object(Path, 'read_text', return_value=membership):
                self.assertIs(self.admin.monitor_process_current({'pid': 23871}, service), False)

    def test_monitor_dead_or_unreadable_process_is_not_current(self):
        service = {'active': 'active', 'controlGroup': '/system.slice/eclat-server.service'}
        for error in (FileNotFoundError('process exited'), PermissionError('proc denied'),
                      UnicodeDecodeError('utf-8', b'\xff', 0, 1, 'invalid data')):
            with self.subTest(error=type(error).__name__), patch.object(Path, 'read_text', side_effect=error):
                self.assertIs(self.admin.monitor_process_current({'pid': 23871}, service), False)

    def test_monitor_cannot_confirm_inactive_or_unknown_unit(self):
        for service in ({'active': 'inactive', 'controlGroup': '/system.slice/eclat-server.service'},
                        {'active': 'unknown', 'controlGroup': '/system.slice/eclat-server.service'},
                        {'active': 'active'}, {'active': 'active', 'controlGroup': '/'}, None):
            with self.subTest(service=service), patch.object(Path, 'read_text') as read:
                self.assertIs(self.admin.monitor_process_current({'pid': 23871}, service), False)
                read.assert_not_called()

    def test_monitor_does_not_trust_stored_process_flag_when_unit_read_fails(self):
        (self.data / 'monitor.json').write_text(json.dumps({'pid': 23871, 'processCurrent': True}))
        for error in (OSError('systemctl unavailable'), MODULE.subprocess.TimeoutExpired('systemctl', 15)):
            with self.subTest(error=type(error).__name__), patch.object(self.admin, 'service_state', side_effect=error):
                self.assertIs(self.admin.monitor()['processCurrent'], False)

    def test_missing_invalid_or_unreadable_monitor_stays_unknown(self):
        self.assertEqual(self.admin.monitor({'active': 'active'}), {})
        source = self.data / 'monitor.json'
        for content in ('not JSON', 'null', '[]'):
            with self.subTest(content=content):
                source.write_text(content)
                self.assertEqual(self.admin.monitor({'active': 'active'}), {'error': 'Monitor unavailable'})
        source.write_text('{}')
        with patch.object(Path, 'read_text', side_effect=PermissionError('monitor denied')):
            self.assertEqual(self.admin.monitor({'active': 'active'}), {'error': 'Monitor unavailable'})

    def test_status_reuses_one_unit_snapshot_for_monitor(self):
        service = {'active': 'active', 'pid': 23846, 'controlGroup': '/system.slice/eclat-server.service'}
        with patch('builtins.open', mock_open(read_data='1000.00 1000.00')), \
                patch.object(self.admin, 'service_state', return_value=service) as query, \
                patch.object(self.admin, 'monitor', return_value={'processCurrent': False}) as monitor, \
                patch.object(self.admin, 'protocol', return_value={'ok': True}), \
                patch.object(self.admin, 'metrics', return_value={}), \
                patch.object(self.admin, 'command', return_value='v24.14.0'):
            result = self.admin.status()
        query.assert_called_once_with()
        monitor.assert_called_once_with(service)
        self.assertEqual(result['service'], service)
        self.assertIs(result['monitor']['processCurrent'], False)

    def test_rejects_unknown_actions_and_fields(self):
        for payload in ({'action': 'shell'}, {'action': 'start', 'command': 'id'}, {'action': ['status']}, {}, [], None):
            with self.subTest(payload=payload), self.assertRaises(ValueError):
                self.admin.dispatch(payload)

    def test_rejects_snapshot_traversal_and_shell_input(self):
        for name in ('../coop.sqlite', '/etc/shadow', 'x;id', 'eclat-20261023T120000Z-12345678.tar.gz/../../x', None):
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.admin.download(name)

    def test_configuration_paths_cannot_escape(self):
        for extra in ({'database': '../outside.sqlite'}, {'privateFiles': ['../secret.json']}, {'service': 'game;id.service'}):
            with self.subTest(extra=extra), self.assertRaises(ValueError):
                MODULE.Admin({'dataDir': str(self.data), **extra})

    def test_backup_checks_integrity_and_includes_private_configuration(self):
        snapshot = self.admin.backup()
        self.assertEqual(snapshot['integrity'], 'ok')
        self.assertEqual(snapshot['sha256'], hashlib.sha256((self.backups / snapshot['name']).read_bytes()).hexdigest())
        with tarfile.open(self.backups / snapshot['name'], 'r:gz') as archive:
            self.assertIn('config/coop.sqlite.identity.json', archive.getnames())
            self.assertIn('config/coop-publish.json', archive.getnames())
            self.assertNotIn('monitor.json', archive.getnames())
        self.assertEqual(self.admin.backups()['items'][0]['name'], snapshot['name'])

    def test_download_is_sqlite_only_and_hash_matches(self):
        snapshot = self.admin.backup()
        download = self.admin.download(snapshot['name'])
        raw = base64.b64decode(download['base64'])
        self.assertTrue(raw.startswith(b'SQLite format 3\x00'))
        self.assertNotIn(b'private-test-secret', raw)
        self.assertEqual(download['sha256'], hashlib.sha256(raw).hexdigest())

    def test_latest_backup_uses_modification_time_instead_of_random_name_suffix(self):
        self.admin.ensure_backup_dir()
        older = self.backups / 'eclat-20261023T120000Z-ffffffff.tar.gz'
        newer = self.backups / 'eclat-20261023T120000Z-00000000.tar.gz'
        older.write_bytes(b'older archive fixture')
        newer.write_bytes(b'newer archive fixture')
        # Both names have the same second, and lexical order would choose the older archive.
        timestamp = 1792756800 * 1_000_000_000
        os.utime(older, ns=(timestamp, timestamp))
        os.utime(newer, ns=(timestamp + 100_000_000, timestamp + 100_000_000))
        self.assertEqual([item['name'] for item in self.admin.backups()['items']], [newer.name, older.name])

    def test_restore_preserves_rows_and_current_server_identity(self):
        snapshot = self.admin.backup()
        with database(self.db) as connection:
            connection.execute('UPDATE players SET eclats=0')
        identity = self.data / 'coop.sqlite.identity.json'
        identity.write_text(json.dumps({'serverId': 'same-current-identity'}))
        with patch.object(self.admin, 'service_state', return_value={'active': 'inactive'}), patch.object(self.admin, 'command') as command:
            result = self.admin.restore(snapshot['name'])
            self.assertTrue(result['restored'])
            self.assertEqual(command.call_count, 1)
        self.assertEqual(self.rows(), [('Alice', 123), ('Benoît', 456)])
        self.assertEqual(json.loads(identity.read_text())['serverId'], 'same-current-identity')
        self.assertTrue((self.backups / result['rollbackBackup']).is_file())

    def test_failed_restored_service_rolls_back_latest_rows(self):
        snapshot = self.admin.backup()
        with database(self.db) as connection:
            connection.execute('UPDATE players SET eclats=999 WHERE id=1')
        with patch.object(self.admin, 'service_state', side_effect=[{'active': 'active'}, {'active': 'inactive'}, {'active': 'inactive'}]), patch.object(self.admin, 'command'), patch.object(self.admin, 'wait_healthy', side_effect=[RuntimeError('health failed'), None]):
            with self.assertRaisesRegex(RuntimeError, 'health failed'):
                self.admin.restore(snapshot['name'])
        self.assertEqual(self.rows(), [('Alice', 999), ('Benoît', 456)])
        self.assertFalse((self.data / '.coop.sqlite.rollback').exists())

    def test_restore_restarts_previously_running_service(self):
        snapshot = self.admin.backup()
        with patch.object(self.admin, 'service_state', side_effect=[{'active': 'active'}, {'active': 'inactive'}]), patch.object(self.admin, 'command') as command, patch.object(self.admin, 'wait_healthy') as health:
            self.admin.restore(snapshot['name'])
            self.assertEqual([call.args[1] for call in command.call_args_list], ['stop', 'start'])
            health.assert_called_once()
        self.assertEqual(self.rows(), [('Alice', 123), ('Benoît', 456)])

    def test_restore_stages_candidate_in_private_backup_directory(self):
        snapshot = self.admin.backup()
        with patch.object(self.admin, 'service_state', return_value={'active': 'inactive'}), patch.object(self.admin, 'command'), patch.object(self.admin, 'verify_database', wraps=self.admin.verify_database) as verify:
            self.admin.restore(snapshot['name'])
            candidate = verify.call_args_list[0].args[0]
        self.assertEqual(candidate.parent, self.backups)
        self.assertFalse(candidate.exists())

    @unittest.skipUnless(hasattr(os, 'O_NOFOLLOW') and hasattr(os, 'getuid'), 'Linux ownership protection')
    def test_database_owner_change_refuses_symlinks(self):
        import pwd
        self.admin.config['serviceUser'] = pwd.getpwuid(os.getuid()).pw_name
        destination = self.root / 'unrelated-file'
        destination.write_text('must remain unchanged')
        before = destination.stat()
        link = self.data / 'unsafe-database'
        link.symlink_to(destination)
        with self.assertRaises(OSError):
            MODULE.Admin.set_database_owner(self.admin, link)
        self.assertEqual(destination.read_text(), 'must remain unchanged')
        self.assertEqual(destination.stat().st_mode, before.st_mode)

    def test_backup_failure_does_not_replace_rows_and_restarts_service(self):
        snapshot = self.admin.backup()
        with database(self.db) as connection:
            connection.execute('UPDATE players SET eclats=321 WHERE id=1')
        with patch.object(self.admin, 'service_state', side_effect=[{'active': 'active'}, {'active': 'inactive'}]), patch.object(self.admin, 'command') as command, patch.object(self.admin, 'wait_healthy'), patch.object(self.admin, 'backup', side_effect=OSError('disk full')):
            with self.assertRaisesRegex(OSError, 'disk full'):
                self.admin.restore(snapshot['name'])
            self.assertEqual([call.args[1] for call in command.call_args_list], ['stop', 'start'])
        self.assertEqual(self.rows(), [('Alice', 321), ('Benoît', 456)])

    def test_refuses_database_replacement_when_service_does_not_stop(self):
        snapshot = self.admin.backup()
        with database(self.db) as connection:
            connection.execute('UPDATE players SET eclats=654 WHERE id=1')
        with patch.object(self.admin, 'service_state', return_value={'active': 'active', 'pid': 123}), patch.object(self.admin, 'command'), patch.object(self.admin, 'backup') as backup:
            with self.assertRaisesRegex(RuntimeError, 'stop could not be confirmed'):
                self.admin.restore(snapshot['name'])
            backup.assert_not_called()
        self.assertEqual(self.rows(), [('Alice', 654), ('Benoît', 456)])

    def test_rollback_keeps_committed_wal_pages(self):
        snapshot = self.admin.backup()
        connection = sqlite3.connect(self.db)
        self.addCleanup(connection.close)
        connection.execute('PRAGMA journal_mode=WAL')
        connection.execute('UPDATE players SET eclats=789 WHERE id=1')
        connection.commit()
        # backup includes committed WAL data; service stop in production closes its SQLite handles.
        consolidated = self.admin.backup()
        raw = self.admin.snapshot_database(self.backups / consolidated['name'])
        copied = self.root / 'wal-check.sqlite'
        copied.write_bytes(raw)
        with database(copied) as check:
            self.assertEqual(check.execute('SELECT eclats FROM players WHERE id=1').fetchone()[0], 789)

    def test_tampered_snapshot_rejected_before_service_stop(self):
        name = 'eclat-20261023T120000Z-12345678.tar.gz'
        self.admin.ensure_backup_dir()
        with tarfile.open(self.backups / name, 'w:gz') as archive:
            for filename, content in (('coop.sqlite', self.db.read_bytes()), ('manifest.json', b'{"databaseSha256":"bad"}')):
                info = tarfile.TarInfo(filename)
                info.size = len(content)
                archive.addfile(info, io.BytesIO(content))
        with patch.object(self.admin, 'command') as command:
            with self.assertRaisesRegex(ValueError, 'checksum'):
                self.admin.restore(name)
            command.assert_not_called()

    def test_unsafe_tar_member_rejected(self):
        name = 'eclat-20261023T120000Z-12345678.tar.gz'
        self.admin.ensure_backup_dir()
        with tarfile.open(self.backups / name, 'w:gz') as archive:
            info = tarfile.TarInfo('../../outside')
            info.size = 1
            archive.addfile(info, io.BytesIO(b'x'))
        with self.assertRaisesRegex(ValueError, 'Unsafe'):
            self.admin.download(name)

    def test_redacts_labelled_secrets_and_nested_values(self):
        value = MODULE.redact({'publishKey': 'secret-1', 'safe': {'token': 'secret-2'}, 'line': 'token=secret-3 https://user:pass@example.test/?key=secret-4'})
        text = json.dumps(value)
        for secret in ('secret-1', 'secret-2', 'secret-3', 'secret-4', 'user:pass'):
            self.assertNotIn(secret, text)

    def test_logs_redact_literal_configuration_secrets(self):
        with patch.object(self.admin, 'command', return_value='public log private-test-secret and token=unknown-secret'):
            result = self.admin.logs()['text']
        self.assertNotIn('private-test-secret', result)
        self.assertNotIn('unknown-secret', result)

    def test_watchdog_does_not_start_intentionally_stopped_server(self):
        with patch.object(self.admin, 'service_state', return_value={'active': 'inactive'}), patch.object(self.admin, 'command') as command:
            self.admin.watchdog()
            command.assert_not_called()

    def test_watchdog_restarts_after_three_protocol_failures(self):
        with patch.object(self.admin, 'service_state', return_value={'active': 'active'}), patch.object(self.admin, 'protocol', return_value={'ok': False}), patch.object(self.admin, 'command') as command:
            self.admin.watchdog()
            self.admin.watchdog()
            command.assert_not_called()
            self.admin.watchdog()
            self.assertEqual(command.call_args.args, ('/usr/bin/systemctl', 'restart', 'eclat-server.service'))
        self.assertFalse((self.root / 'watchdog.json').exists())

    def test_protocol_sends_current_hello_and_ping(self):
        stream = io.BytesIO()
        stream.readline = lambda limit: json.dumps({'type': 'hello.ok' if stream.getvalue().count(b'\n') == 1 else 'ping.ok'}).encode() + b'\n'
        connection = unittest.mock.Mock()
        connection.makefile.return_value = stream
        self.admin.probe_connection(connection)
        sent = [json.loads(line) for line in stream.getvalue().splitlines()]
        self.assertEqual(sent[0]['payload']['name'], 'AdminCheck')
        self.assertEqual(sent[1]['type'], 'ping')

    def test_protocol_recognizes_deliberate_maintenance_without_sending_ping(self):
        stream = io.BytesIO()
        stream.readline = lambda limit: b'{"v":1,"id":"admin_hello","type":"error","payload":{"code":"MAINTENANCE"}}\n'
        connection = unittest.mock.Mock()
        connection.makefile.return_value = stream
        result = self.admin.probe_connection(connection)
        self.assertEqual(result, {'maintenance': True})
        self.assertEqual(len(stream.getvalue().splitlines()), 1)
        for code in ('ACCOUNT_BANNED', 'UNKNOWN_ERROR', ''):
            stream = io.BytesIO()
            stream.readline = lambda limit: json.dumps({'type': 'error', 'payload': {'code': code}}).encode() + b'\n'
            connection.makefile.return_value = stream
            with self.subTest(code=code), self.assertRaises(RuntimeError):
                self.admin.probe_connection(connection)

    def test_watchdog_preserves_active_parties_during_three_maintenance_checks(self):
        with patch.object(self.admin, 'service_state', return_value={'active': 'active'}), patch.object(self.admin, 'protocol', return_value={'ok': True, 'maintenance': True}), patch.object(self.admin, 'command') as command:
            for _ in range(3):
                self.admin.watchdog()
            command.assert_not_called()
        self.assertFalse((self.root / 'watchdog.json').exists())


if __name__ == '__main__':
    unittest.main()
