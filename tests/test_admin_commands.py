"""Local-only command/configuration tests. All files and sockets are temporary."""
import contextlib
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import socket
import sqlite3
import stat
import subprocess
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
import uuid

from test_admin import MODULE, database


@unittest.skipUnless(os.name == 'posix', 'Linux administration contract')
class TestAdministrationCommands(unittest.TestCase):
    def setUp(self):
        import pwd
        self.temp = tempfile.TemporaryDirectory(prefix='eclat-admin-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.data = self.root / 'data'
        self.data.mkdir()
        self.validator = self.root / 'admin-validate-config.js'
        self.validator.write_text('// validator fixture: subprocess is mocked in unit tests', encoding='utf-8')
        self.admin = MODULE.Admin({'dataDir': str(self.data), 'backupDir': str(self.root / 'backups'),
                                  'serviceUser': pwd.getpwuid(os.getuid()).pw_name,
                                  'operationLock': str(self.root / 'operation.lock'),
                                  'configValidator': str(self.validator), 'nodeBinary': '/usr/bin/node'})
        self.original = b'{"version":1,"events":[]}\n'
        self.admin.config_path.write_bytes(self.original)
        os.chmod(self.admin.config_path, 0o640)
        self.sha = hashlib.sha256(self.original).hexdigest()

    def request(self, action='config.save', **values):
        return {'action': action, 'requestId': str(uuid.uuid4()), 'config': {'version': 1, 'events': [], 'prices': {'single': 30}},
                'expectedSha256': self.sha, 'reason': 'Évènement de test', **values}

    def accepted(self, checksum):
        return {'state': 'accepted', 'sha256': checksum, 'loadedSha256': checksum, 'updatedAt': dt.datetime.now(dt.timezone.utc).isoformat()}

    @contextlib.contextmanager
    def fake_socket(self, response, mode=0o600, delay=0):
        listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        listener.bind(str(self.admin.socket_path))
        os.chmod(self.admin.socket_path, mode)
        listener.listen(1)
        received = []
        errors = []
        def serve():
            try:
                listener.settimeout(2)
                connection, _ = listener.accept()
                with connection:
                    raw = bytearray()
                    while b'\n' not in raw:
                        raw.extend(connection.recv(65536))
                    received.append(json.loads(raw))
                    if delay:
                        time.sleep(delay)
                    try:
                        connection.sendall(response)
                    except (BrokenPipeError, ConnectionResetError):
                        pass
            except (OSError, ValueError) as error:
                errors.append(error)
        thread = threading.Thread(target=serve)
        thread.start()
        try:
            yield received
        finally:
            listener.close()
            thread.join(3)
            self.admin.socket_path.unlink(missing_ok=True)

    def test_all_eight_commands_validate_and_forward_exactly_once(self):
        commands = {
            'player.kick': {'playerId': '00000000-0000-4000-8000-000000000001'},
            'account.ban': {'accountHash': 'a' * 64, 'reason': 'Abus répétés', 'expiresAt': None},
            'account.unban': {'accountHash': 'a' * 64, 'reason': 'Dossier réexaminé'},
            'room.close': {'roomId': '00000000-0000-4000-8000-000000000002'},
            'room.update': {'roomId': '00000000-0000-4000-8000-000000000002', 'capacity': 10, 'visibility': 'private'},
            'maintenance.set': {'enabled': True},
            'eclats.adjust': {'accountHash': 'a' * 64, 'amount': -10, 'reason': 'Correction comptable'},
            'casino.refund': {'accountHash': 'a' * 64, 'amount': 12, 'reason': 'Partie interrompue'},
        }
        for command, arguments in commands.items():
            with self.subTest(command=command), patch.object(self.admin, 'admin_exchange', return_value={'done': True}) as exchange:
                request_id = str(uuid.uuid4())
                result = self.admin.dispatch({'action': 'admin.command', 'requestId': request_id, 'command': command, 'arguments': arguments})
                self.assertEqual(result, {'done': True})
                exchange.assert_called_once_with(command, arguments, request_id)

    def test_invalid_commands_do_not_reach_socket(self):
        bad = [
            ('shell', {}), ('player.kick', {'playerId': ''}), ('player.kick', {'playerId': 'x', 'shell': 'id'}),
            ('account.ban', {'accountHash': 'raw-account-key', 'reason': 'test', 'expiresAt': None}),
            ('account.ban', {'accountHash': 'a' * 64, 'reason': '', 'expiresAt': None}),
            ('account.ban', {'accountHash': 'a' * 64, 'reason': 'test', 'expiresAt': '2000-01-01T00:00:00Z'}),
            ('account.ban', {'accountHash': 'a' * 64, 'reason': 'test', 'expiresAt': '2099-01-01'}),
            ('account.unban', {'accountHash': 'a' * 64, 'accountKey': 'private'}),
            ('room.close', {'roomId': 'x\nsecret'}), ('room.update', {'roomId': '00000000-0000-4000-8000-000000000002'}),
            ('room.update', {'roomId': '00000000-0000-4000-8000-000000000002', 'capacity': 11}), ('room.update', {'roomId': '00000000-0000-4000-8000-000000000002', 'capacity': True}),
            ('room.update', {'roomId': '00000000-0000-4000-8000-000000000002', 'capacity': 1}),
            ('room.update', {'roomId': '00000000-0000-4000-8000-000000000002', 'visibility': 'friends'}), ('maintenance.set', {'enabled': 1}),
            ('eclats.adjust', {'accountHash': 'a' * 64, 'amount': 0, 'reason': 'test'}),
            ('eclats.adjust', {'accountHash': 'a' * 64, 'amount': -2147483648, 'reason': 'test'}),
            ('casino.refund', {'accountHash': 'a' * 64, 'amount': -1, 'reason': 'test'}),
            ('casino.refund', {'accountHash': 'a' * 64, 'amount': 10000, 'reason': 'test'}),
        ]
        with patch.object(self.admin, 'admin_exchange') as exchange:
            for command, arguments in bad:
                with self.subTest(command=command, arguments=arguments), self.assertRaises(ValueError):
                    self.admin.dispatch({'action': 'admin.command', 'requestId': str(uuid.uuid4()), 'command': command, 'arguments': arguments})
            exchange.assert_not_called()

    def test_uuid_and_top_level_action_fields_are_closed(self):
        for action in ('admin.overview', 'admin.audit', 'config.get', 'config.history', 'statistics'):
            with self.subTest(action=action), self.assertRaises(ValueError):
                self.admin.dispatch({'action': action, 'path': '/etc/shadow'})
        with patch.object(self.admin, 'admin_exchange') as exchange:
            for request_id in ('id', None, 'x' * 36):
                with self.subTest(request_id=request_id), self.assertRaises(ValueError):
                    self.admin.dispatch({'action': 'admin.command', 'requestId': request_id, 'command': 'maintenance.set', 'arguments': {'enabled': True}})
            exchange.assert_not_called()

    def test_previous_windows_transport_optional_null_name_remains_compatible(self):
        for action in MODULE.LEGACY_NO_NAME:
            with self.subTest(action=action), patch.object(MODULE, 'operation_lock'), patch.object(self.admin, 'dispatch_action', return_value={'legacy': True}) as dispatch:
                self.assertEqual(self.admin.dispatch({'action': action, 'name': None}), {'legacy': True})
                dispatch.assert_called_once_with({'action': action, 'name': None})
            with self.subTest(action=action), self.assertRaises(ValueError):
                self.admin.dispatch({'action': action, 'name': 'unexpected'})
        for action in ('admin.overview', 'admin.audit', 'config.get', 'config.history', 'statistics'):
            with self.subTest(action=action), self.assertRaises(ValueError):
                self.admin.dispatch({'action': action, 'name': None})

    def test_socket_sends_versioned_request_and_masks_account_key(self):
        response = {'ok': True, 'data': {'maintenance': False, 'accountKey': 'private-test-value'}}
        with self.fake_socket(json.dumps(response).encode() + b'\n') as received:
            result = self.admin.dispatch({'action': 'admin.overview'})
        self.assertEqual(len(received), 1)
        self.assertEqual(received[0]['version'], 1)
        self.assertEqual(received[0]['command'], 'admin.overview')
        self.assertEqual(received[0]['arguments'], {})
        uuid.UUID(received[0]['requestId'])
        self.assertNotIn('private-test-value', json.dumps(result))
        self.assertTrue(result['capabilities']['commands'])

    def test_socket_error_code_preserved_without_retry(self):
        with self.fake_socket(b'{"ok":false,"error":{"code":"ACCOUNT_UNKNOWN","message":"Compte inconnu"}}\n') as received:
            with self.assertRaisesRegex(RuntimeError, 'ACCOUNT_UNKNOWN'):
                self.admin.admin_exchange('account.unban', {'accountHash': 'a' * 64})
        self.assertEqual(len(received), 1)

    def test_socket_reply_size_and_json_are_bounded(self):
        for response in (b'x' * (MODULE.MAX_ADMIN_RESPONSE + 1), b'not-json\n', b'{"ok":true,"data":null}\n', b'{"ok":1}\n'):
            with self.subTest(length=len(response)), self.fake_socket(response), self.assertRaises(RuntimeError):
                self.admin.admin_exchange('admin.overview', {})

    def test_missing_unsafe_or_wrong_owner_socket_is_refused(self):
        with self.assertRaisesRegex(RuntimeError, 'compatible game server'):
            self.admin.admin_exchange('admin.overview', {})
        self.admin.socket_path.write_text('not a socket')
        try:
            with self.assertRaisesRegex(ValueError, 'Unsafe'):
                self.admin.admin_exchange('admin.overview', {})
        finally:
            self.admin.socket_path.unlink()
        with self.fake_socket(b'{}\n', mode=0o666), self.assertRaisesRegex(ValueError, '0600'):
            self.admin.admin_exchange('admin.overview', {})
        with self.fake_socket(b'{}\n'), patch('pwd.getpwnam') as owner, self.assertRaisesRegex(ValueError, 'owned'):
            owner.return_value.pw_uid = os.getuid() + 1
            self.admin.admin_exchange('admin.overview', {})

    def test_socket_timeout_reports_unknown_result_without_retry(self):
        with self.fake_socket(b'{"ok":true,"data":{}}\n', delay=.1) as received, patch.object(MODULE, 'ADMIN_DEADLINE', .02):
            with self.assertRaisesRegex(RuntimeError, 'result unknown'):
                self.admin.admin_exchange('maintenance.set', {'enabled': True})
        self.assertEqual(len(received), 1)

    def test_legacy_close_room_uses_same_bounded_versioned_bridge(self):
        with patch.object(self.admin, 'admin_exchange', return_value={'closed': True, 'roomId': '00000000-0000-4000-8000-000000000002'}) as exchange:
            self.assertEqual(self.admin.dispatch({'action': 'close-room', 'name': '00000000-0000-4000-8000-000000000002'}), {'closed': True, 'roomId': '00000000-0000-4000-8000-000000000002'})
            exchange.assert_called_once_with('room.close', {'roomId': '00000000-0000-4000-8000-000000000002'})

    def test_every_mutating_action_is_locked(self):
        for action in MODULE.MUTATIONS:
            with self.subTest(action=action), patch.object(MODULE, 'operation_lock') as lock, patch.object(self.admin, 'dispatch_action', return_value={}):
                lock.return_value.__enter__.return_value = None
                self.admin.dispatch({'action': action})
                lock.assert_called_once_with(self.admin.lock_path)

    def test_overlapping_operations_and_symlink_lock_are_refused(self):
        with MODULE.operation_lock(self.admin.lock_path):
            with self.assertRaisesRegex(RuntimeError, 'Another administration'):
                self.admin.dispatch({'action': 'admin.command', 'requestId': str(uuid.uuid4()), 'command': 'maintenance.set', 'arguments': {'enabled': True}})
        self.admin.lock_path.unlink()
        target = self.root / 'unrelated'
        target.write_text('unchanged')
        self.admin.lock_path.symlink_to(target)
        with self.assertRaises(OSError), MODULE.operation_lock(self.admin.lock_path):
            pass
        self.assertEqual(target.read_text(), 'unchanged')

    def test_config_get_and_history_never_expose_other_private_files(self):
        (self.data / 'eclats-account.key').write_text('private-account-contents')
        with patch.object(self.admin, 'monitor', return_value={}):
            result = self.admin.dispatch({'action': 'config.get'})
        self.assertEqual(result['config'], {'version': 1, 'events': []})
        self.assertEqual(result['sha256'], self.sha)
        self.assertEqual(result['status']['state'], 'pending')
        self.assertNotIn('private-account-contents', json.dumps(result))
        self.assertEqual(self.admin.dispatch({'action': 'config.history'}), {'items': []})

    def test_validated_config_is_atomic_preserves_owner_mode_and_waits_for_acceptance(self):
        previous = self.admin.config_path.stat()
        with patch.object(self.admin, 'validate_config') as validator, patch.object(self.admin, 'config_status', side_effect=self.accepted):
            result = self.admin.dispatch(self.request())
        validator.assert_called_once()
        new_bytes = self.admin.config_path.read_bytes()
        self.assertEqual(validator.call_args.args[0], new_bytes)
        self.assertTrue(result['accepted'])
        self.assertEqual(result['sha256'], hashlib.sha256(new_bytes).hexdigest())
        self.assertEqual(stat.S_IMODE(self.admin.config_path.stat().st_mode), stat.S_IMODE(previous.st_mode))
        self.assertEqual(self.admin.config_path.stat().st_uid, previous.st_uid)
        history = self.admin.config_history()['items']
        self.assertEqual(len(history), 2)
        self.assertEqual(history[0]['sha256'], result['sha256'])
        self.assertEqual(history[1]['sha256'], self.sha)
        self.assertEqual(self.admin.config_audit_records()[-1]['result']['code'], 'ACCEPTED')
        self.assertFalse(list(self.data.glob('.eclats.json-*')))

    def test_file_written_alone_is_never_reported_as_accepted(self):
        for state in ('pending', 'rejected'):
            with self.subTest(state=state), patch.object(self.admin, 'validate_config'), patch.object(self.admin, 'config_status', side_effect=lambda checksum: {'state': state, 'sha256': checksum}), patch.object(MODULE.time, 'monotonic', side_effect=[0, 10]):
                result = self.admin.dispatch(self.request(expectedSha256=hashlib.sha256(self.admin.config_path.read_bytes()).hexdigest()))
            self.assertFalse(result['accepted'])
            self.assertEqual(result['status']['state'], state)
            self.assertEqual(self.admin.config_audit_records()[-1]['result']['code'], 'REJECTED' if state == 'rejected' else 'PENDING_RELOAD')

    def test_invalid_config_or_validator_missing_does_not_change_file(self):
        for error in (ValueError('INVALID_ECLATS_CONFIG'), RuntimeError('Validator missing')):
            with self.subTest(error=str(error)), patch.object(self.admin, 'validate_config', side_effect=error), self.assertRaises(type(error)):
                self.admin.dispatch(self.request())
            self.assertEqual(self.admin.config_path.read_bytes(), self.original)
            self.assertEqual(self.admin.config_audit_records()[-1]['result']['code'], 'CONFIG_INVALID')
            self.assertEqual(self.admin.config_history()['items'], [])

    def test_compare_and_swap_refuses_stale_edits_and_mid_validation_change(self):
        with patch.object(self.admin, 'validate_config') as validate, self.assertRaisesRegex(ValueError, 'CONFIG_CONFLICT'):
            self.admin.dispatch(self.request(expectedSha256='0' * 64))
        validate.assert_not_called()
        self.assertEqual(self.admin.config_path.read_bytes(), self.original)
        external = b'{"version":1,"externallyChanged":true}\n'
        with patch.object(self.admin, 'validate_config', side_effect=lambda content: self.admin.config_path.write_bytes(external)), self.assertRaisesRegex(ValueError, 'CONFIG_CONFLICT'):
            self.admin.dispatch(self.request())
        self.assertEqual(self.admin.config_path.read_bytes(), external)
        self.assertEqual(self.admin.config_audit_records()[-1]['result']['code'], 'CONFIG_CONFLICT')

    def test_config_requires_reason_sha_uuid_and_rejects_duplicate_id(self):
        for changes in ({'reason': ''}, {'reason': 'bad\nreason'}, {'expectedSha256': None}, {'requestId': 'x'}, {'config': []}):
            with self.subTest(changes=changes), patch.object(self.admin, 'validate_config'), self.assertRaises(ValueError):
                self.admin.dispatch(self.request(**changes))
            self.assertEqual(self.admin.config_path.read_bytes(), self.original)
        request = self.request()
        with patch.object(self.admin, 'validate_config'), patch.object(self.admin, 'config_status', side_effect=self.accepted):
            self.admin.dispatch(request)
            written = self.admin.config_path.read_bytes()
            with self.assertRaisesRegex(ValueError, 'already used'):
                self.admin.dispatch(request)
        self.assertEqual(self.admin.config_path.read_bytes(), written)

    def test_config_symlink_history_tampering_and_path_traversal_are_refused(self):
        target = self.root / 'outside.json'
        target.write_bytes(self.original)
        self.admin.config_path.unlink()
        self.admin.config_path.symlink_to(target)
        with patch.object(self.admin, 'validate_config'), self.assertRaises(OSError):
            self.admin.dispatch(self.request())
        self.assertEqual(target.read_bytes(), self.original)
        version = self.admin.record_config_version(self.original, 'test', str(uuid.uuid4()))
        item = json.loads((self.admin.history_dir / version).read_text())
        item['content'] = '{"tampered":true}'
        (self.admin.history_dir / version).write_text(json.dumps(item))
        with self.assertRaisesRegex(ValueError, 'checksum'):
            self.admin.checked_config_version(version)
        with self.assertRaisesRegex(ValueError, 'Invalid configuration version'):
            self.admin.checked_config_version('../../etc/shadow')

    def test_twenty_complete_versions_retained_and_restore_is_validated(self):
        oldest = None
        for index in range(23):
            content = json.dumps({'version': 1, 'sequence': index}).encode()
            version = self.admin.record_config_version(content, f'Change {index}', str(uuid.uuid4()))
            oldest = oldest or version
        history = self.admin.config_history()['items']
        self.assertEqual(len(history), 20)
        self.assertFalse((self.admin.history_dir / oldest).exists())
        item, content = self.admin.checked_config_version(history[-1]['id'])
        request = self.request(action='config.restore', version=item['id'])
        del request['config']
        with patch.object(self.admin, 'validate_config') as validator, patch.object(self.admin, 'config_status', side_effect=self.accepted):
            result = self.admin.dispatch(request)
        validator.assert_called_once_with(content)
        self.assertEqual(self.admin.config_path.read_bytes(), content)
        self.assertTrue(result['accepted'])
        self.assertEqual(len(self.admin.config_history()['items']), 20)
        self.assertEqual(self.admin.config_audit_records()[-1]['command'], 'config.restore')

    def test_atomic_replace_failure_keeps_current_file_and_cleans_temporary(self):
        with patch.object(MODULE.os, 'replace', side_effect=OSError('disk failed')), self.assertRaisesRegex(OSError, 'disk failed'):
            self.admin.atomic_write(self.admin.config_path, b'new config', self.admin.config_path.stat())
        self.assertEqual(self.admin.config_path.read_bytes(), self.original)
        self.assertFalse(list(self.data.glob('.eclats.json-*')))

    def test_nonregular_config_and_audit_are_refused_without_waiting_for_a_writer(self):
        self.admin.config_path.unlink()
        os.mkfifo(self.admin.config_path)
        with self.assertRaisesRegex(ValueError, 'Unsafe'):
            self.admin.current_config_bytes()
        self.admin.ensure_backup_dir()
        os.mkfifo(self.admin.audit_path)
        with self.assertRaises(OSError):
            self.admin.config_audit(str(uuid.uuid4()), 'config.save', 'test', {'ok': False})

    def test_config_acceptance_requires_current_service_and_matching_loaded_hash(self):
        state = self.accepted(self.sha)
        fresh = dt.datetime.now(dt.timezone.utc).isoformat()
        for monitor in ({}, {'processCurrent': False, 'eclatsConfig': state}, {'processCurrent': True, 'updatedAt': fresh, 'eclatsConfig': {**state, 'sha256': '1' * 64}},
                        {'processCurrent': True, 'updatedAt': '2000-01-01T00:00:00Z', 'eclatsConfig': state},
                        {'processCurrent': True, 'updatedAt': fresh, 'eclatsConfig': {**state, 'loadedSha256': 'b' * 64}}):
            with self.subTest(monitor=monitor), patch.object(self.admin, 'monitor', return_value=monitor):
                self.assertEqual(self.admin.config_status(self.sha)['state'], 'pending')
        with patch.object(self.admin, 'monitor', return_value={'processCurrent': True, 'updatedAt': fresh, 'eclatsConfig': state}):
            self.assertEqual(self.admin.config_status(self.sha)['state'], 'accepted')

    def test_missing_config_uses_game_default_and_empty_file_checksum_for_creation(self):
        self.admin.config_path.unlink()
        default = {'version': 1, 'events': [], 'prices': {'single': 30}}
        with patch.object(self.admin, 'run_config_validator', return_value={'ok': True, 'config': default}) as validator, patch.object(self.admin, 'monitor', return_value={}):
            result = self.admin.dispatch({'action': 'config.get'})
        validator.assert_called_once_with('--default')
        self.assertEqual(result['config'], default)
        self.assertEqual(result['sha256'], hashlib.sha256(b'').hexdigest())
        self.assertFalse(result['exists'])
        self.assertFalse(self.admin.config_path.exists())
        with patch.object(self.admin, 'validate_config'), patch.object(self.admin, 'config_status', side_effect=self.accepted):
            result = self.admin.dispatch(self.request(expectedSha256=hashlib.sha256(b'').hexdigest()))
        self.assertTrue(result['accepted'])
        self.assertEqual(stat.S_IMODE(self.admin.config_path.stat().st_mode), 0o600)

    def test_invalid_json_keeps_actual_checksum_raw_and_proposes_real_default_for_repair(self):
        default = {'version': 1, 'events': []}
        for invalid in (b'{broken-json', b'null', b'[]', b'', b'\xff'):
            self.admin.config_path.write_bytes(invalid)
            checksum = hashlib.sha256(invalid).hexdigest()
            with self.subTest(invalid=invalid), patch.object(self.admin, 'run_config_validator', return_value={'ok': True, 'config': default}), patch.object(self.admin, 'config_status', return_value=self.accepted(checksum)):
                result = self.admin.dispatch({'action': 'config.get'})
            self.assertIsNone(result['config'])
            self.assertTrue(result['fileInvalid'])
            self.assertTrue(result['exists'])
            self.assertEqual(result['sha256'], checksum)
            self.assertEqual(result['raw'], invalid.decode('utf-8', errors='replace'))
            self.assertEqual(result['recoveryConfig'], default)
            self.assertNotEqual(result['status']['state'], 'accepted')
            self.assertEqual(self.admin.config_path.read_bytes(), invalid)

    def test_repair_preserves_an_invalid_utf8_previous_version_without_blocking_valid_save(self):
        invalid = b'\xff{broken configuration'
        self.admin.config_path.write_bytes(invalid)
        with patch.object(self.admin, 'validate_config'), patch.object(self.admin, 'config_status', side_effect=self.accepted):
            result = self.admin.dispatch(self.request(expectedSha256=hashlib.sha256(invalid).hexdigest()))
        self.assertTrue(result['accepted'])
        previous = self.admin.config_history()['items'][-1]
        self.assertEqual(self.admin.checked_config_version(previous['id'])[1], invalid)
        self.assertNotEqual(self.admin.config_path.read_bytes(), invalid)

    def test_game_validator_is_invoked_on_private_candidate_and_checksum_is_required(self):
        content = b'{"version":1}'
        def validated(args, **kwargs):
            self.assertEqual(args[:2], ['/usr/bin/node', str(self.validator)])
            candidate = Path(args[2])
            self.assertEqual(candidate.parent, self.admin.backup_dir)
            self.assertEqual(candidate.read_bytes(), content)
            self.assertEqual(stat.S_IMODE(candidate.stat().st_mode), 0o600)
            return subprocess.CompletedProcess(args, 0, stdout=json.dumps({'ok': True, 'sha256': hashlib.sha256(content).hexdigest()}).encode(), stderr=b'')
        with patch.object(MODULE.subprocess, 'run', side_effect=validated):
            self.admin.validate_config(content)
        self.assertFalse(list(self.admin.backup_dir.glob('.validate-config-*')))
        for response in (subprocess.CompletedProcess([], 1, b'{"ok":false}', b''), subprocess.CompletedProcess([], 0, b'{"ok":true,"sha256":"bad"}', b'')):
            with self.subTest(response=response.stdout), patch.object(MODULE.subprocess, 'run', return_value=response), self.assertRaises(ValueError):
                self.admin.validate_config(content)
        with self.assertRaisesRegex(ValueError, '64 KiB'):
            self.admin.validate_config(b'x' * (MODULE.MAX_CONFIG + 1))

    def test_audit_combines_latest_local_result_with_server_commands(self):
        request_id = str(uuid.uuid4())
        self.admin.config_audit(request_id, 'config.save', 'token=private-secret', {'ok': False, 'code': 'PENDING'})
        self.admin.config_audit(request_id, 'config.save', 'test', {'ok': True, 'code': 'ACCEPTED'})
        remote = {'at': '2026-01-01T12:00:00Z', 'requestId': str(uuid.uuid4()), 'command': 'player.kick', 'target': 'p1', 'reason': '', 'result': {'ok': True}}
        with patch.object(self.admin, 'admin_exchange', return_value={'items': [remote]}) as exchange:
            audit = self.admin.dispatch({'action': 'admin.audit'})
        exchange.assert_called_once_with('admin.audit', {'limit': 200})
        self.assertEqual(len(audit['items']), 2)
        local = next(item for item in audit['items'] if item['requestId'] == request_id)
        self.assertEqual(local['result']['code'], 'ACCEPTED')
        self.assertNotIn('private-secret', self.admin.audit_path.read_text())
        with patch.object(self.admin, 'admin_exchange', side_effect=RuntimeError('offline')):
            partial = self.admin.dispatch({'action': 'admin.audit'})
        self.assertEqual(len(partial['items']), 1)
        self.assertIn('serverAuditUnavailable', partial)


@unittest.skipUnless(os.name == 'posix', 'Linux administration statistics')
class TestReadOnlyStatistics(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='eclat-stats-')
        self.addCleanup(self.temp.cleanup)
        self.data = Path(self.temp.name)
        self.admin = MODULE.Admin({'dataDir': str(self.data), 'backupDir': str(self.data / 'backups')})
        self.db = self.data / 'coop.sqlite'
        self.now = int(dt.datetime.now(dt.timezone.utc).timestamp() * 1000)
        with database(self.db) as connection:
            connection.executescript('''
                CREATE TABLE eclats_accounts(hash TEXT PRIMARY KEY,ot_id INTEGER,created_at INTEGER);
                CREATE TABLE eclats_ledger(id INTEGER PRIMARY KEY,hash TEXT,delta INTEGER,code INTEGER,ref TEXT,at INTEGER);
                CREATE TABLE eclats_pulls(id INTEGER PRIMARY KEY,hash TEXT,at INTEGER);
                CREATE TABLE eclats_eggs(id INTEGER PRIMARY KEY,hash TEXT,pull_id INTEGER,banner INTEGER,pid INTEGER);
                CREATE TABLE eclats_operations(hash TEXT,key TEXT,request TEXT,response TEXT,at INTEGER);
                CREATE TABLE eclats_receipts(hash TEXT,code INTEGER,ref TEXT,at INTEGER);
                CREATE TABLE casino_games(id INTEGER PRIMARY KEY,hash TEXT,game TEXT,currency TEXT,bet INTEGER,payout INTEGER,detail TEXT,at INTEGER);
            ''')
            connection.executemany('INSERT INTO eclats_accounts VALUES(?,?,?)', [('a' * 64, 0, self.now), ('b' * 64, 0, self.now)])
            connection.executemany('INSERT INTO eclats_ledger(hash,delta,at) VALUES(?,?,?)', [('a' * 64, 100, self.now), ('a' * 64, -20, self.now), ('b' * 64, 50, self.now)])
            connection.executemany('INSERT INTO eclats_pulls VALUES(?,?,?)', [(1, 'a' * 64, self.now), (2, 'b' * 64, self.now)])
            connection.executemany('INSERT INTO eclats_eggs VALUES(?,?,?,?,?)', [(1, 'a' * 64, 1, 1, 0), (2, 'a' * 64, 1, 1, 9), (3, 'b' * 64, 2, 2, 1)])
            connection.executemany('INSERT INTO casino_games VALUES(?,?,?,?,?,?,?,?)', [
                (1, 'a' * 64, 'slots', 'eclats', 10, 20, '{}', self.now),
                (2, 'a' * 64, 'slots', 'eclats', 10, 50, '{"freeSpin":true}', self.now),
                (3, 'b' * 64, 'blackjack', 'coins', 20, 40, '{}', self.now),
                (4, 'c' * 64, 'blackjack', 'coins', 1, 123, '{"adminRefund":true}', self.now)])
            connection.execute('INSERT INTO eclats_operations VALUES(?,?,?,?,?)', ('a' * 64, 'key', '{}', '{}', self.now))

    def test_circulation_draws_shiny_free_spins_and_refunds_are_distinct_and_read_only(self):
        before = self.db.read_bytes()
        result = self.admin.dispatch({'action': 'statistics'})
        self.assertEqual(result['totals'], {'accounts': 2, 'eclatsInCirculation': 130, 'eggs': 3, 'pulls': 2, 'shiny': 2})
        self.assertEqual(result['drawsByBanner'], [{'banner': 1, 'eggs': 2, 'pulls': 1, 'shiny': 1}, {'banner': 2, 'eggs': 1, 'pulls': 1, 'shiny': 1}])
        slots = next(row for row in result['casinoByGame'] if row['game'] == 'slots')
        self.assertEqual(slots, {'game': 'slots', 'currency': 'eclats', 'rounds': 2, 'wagers': 10, 'payouts': 70})
        blackjack = next(row for row in result['casinoByGame'] if row['game'] == 'blackjack')
        self.assertEqual(blackjack['rounds'], 1)
        self.assertEqual(result['refundTotals'], {'refunds': 1, 'coins': 123})
        self.assertEqual(result['activePlayersByDay'][0]['players'], 2)
        self.assertEqual(result['activityScope'], 'economy')
        self.assertEqual(result['dayTimezone'], 'UTC')
        self.assertEqual(self.db.read_bytes(), before)

    def test_queries_use_mode_ro_and_query_only(self):
        original = MODULE.sqlite3.connect
        connections = []
        def open_read_only(*args, **kwargs):
            self.assertTrue(args[0].endswith('?mode=ro'))
            self.assertTrue(kwargs['uri'])
            result = original(*args, **kwargs)
            connections.append(result)
            return result
        with patch.object(MODULE.sqlite3, 'connect', side_effect=open_read_only):
            self.admin.statistics()
        self.assertEqual(len(connections), 1)

    def test_authenticated_activity_history_takes_precedence_over_economy_proxy(self):
        today = dt.datetime.now(dt.timezone.utc).date().isoformat()
        with database(self.db) as connection:
            connection.execute('CREATE TABLE admin_account_activity(hash TEXT,day TEXT,at INTEGER,PRIMARY KEY(hash,day))')
            connection.executemany('INSERT INTO admin_account_activity VALUES(?,?,?)', [('a' * 64, today, self.now), ('b' * 64, today, self.now), ('c' * 64, today, self.now)])
        result = self.admin.statistics()
        self.assertEqual(result['activityScope'], 'authenticatedConnections')
        self.assertEqual(result['activityAvailableSince'], today)
        self.assertEqual(result['activePlayersByDay'], [{'day': today, 'players': 3}])

    def test_incompatible_schema_and_missing_database_are_explicit(self):
        with database(self.db) as connection:
            connection.execute('DROP TABLE eclats_pulls')
        with self.assertRaisesRegex(RuntimeError, 'expected economy tables'):
            self.admin.statistics()
        self.db.unlink()
        with self.assertRaisesRegex(ValueError, 'not found'):
            self.admin.statistics()
        self.assertFalse(self.db.exists())


if __name__ == '__main__':
    unittest.main()
