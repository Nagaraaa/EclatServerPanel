"""Optional real game/validator checks, using a separate build and temporary data.

ECLAT_NODE_BINARY=/absolute/node ECLAT_SERVER_BUILD=/absolute/game/dist \
python3 -m unittest discover -s tests -p test_admin_integration.py -v
"""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import select
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import uuid

from test_admin import MODULE

NODE = os.environ.get('ECLAT_NODE_BINARY')
BUILD = os.environ.get('ECLAT_SERVER_BUILD')
AVAILABLE = os.name == 'posix' and NODE and BUILD and Path(NODE).is_file() and (Path(BUILD) / 'server/src/admin-validate-config.js').is_file()


@unittest.skipUnless(AVAILABLE, 'Set ECLAT_NODE_BINARY and ECLAT_SERVER_BUILD for real cross-repository checks')
class TestGameIntegration(unittest.TestCase):
    def setUp(self):
        import pwd
        self.temp = tempfile.TemporaryDirectory(prefix='eclat-game-integration-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.data = self.root / 'data'
        self.data.mkdir()
        self.admin = MODULE.Admin({'dataDir': str(self.data), 'backupDir': str(self.root / 'backups'),
                                  'operationLock': str(self.root / 'operation.lock'),
                                  'serviceUser': pwd.getpwuid(os.getuid()).pw_name,
                                  'nodeBinary': NODE, 'configValidator': str(Path(BUILD) / 'server/src/admin-validate-config.js')})

    def command(self, command, arguments):
        return self.admin.dispatch({'action': 'admin.command', 'requestId': str(uuid.uuid4()), 'command': command, 'arguments': arguments})

    def test_real_validator_defaults_semantics_checksum_and_size(self):
        with patch.object(self.admin, 'monitor', return_value={}):
            result = self.admin.dispatch({'action': 'config.get'})
        self.assertFalse(result['exists'])
        self.assertEqual(result['sha256'], hashlib.sha256(b'').hexdigest())
        config = result['config']
        self.assertTrue(config['banners'])
        valid = json.dumps(config, ensure_ascii=False).encode('utf-8')
        self.admin.validate_config(valid)
        invalid = {**config, 'prices': {'single': 0, 'ten': 270}}
        with self.assertRaisesRegex(ValueError, 'INVALID_ECLATS_CONFIG'):
            self.admin.validate_config(json.dumps(invalid).encode())
        self.assertFalse(self.admin.config_path.exists())
        with self.assertRaisesRegex(ValueError, '64 KiB'):
            self.admin.validate_config(b'x' * 65537)

    def test_real_socket_commands_config_reload_restore_audit_and_read_only_statistics(self):
        config = self.admin.run_config_validator('--default')['config']
        self.admin.config_path.write_text(json.dumps(config), encoding='utf-8')
        identity = self.data / 'coop.sqlite.identity.json'
        identity.write_text('{"testIdentity":"preserved"}', encoding='utf-8')
        identity_before = identity.read_bytes()
        with tempfile.TemporaryFile() as errors:
            child = subprocess.Popen([NODE, str(Path(__file__).with_name('integration-game-fixture.mjs')), BUILD, str(self.data)],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors)
            try:
                self.assertTrue(select.select([child.stdout], [], [], 15)[0], 'Local game did not start')
                line = child.stdout.readline()
                if not line:
                    errors.seek(0)
                    self.fail('Local game stopped: ' + errors.read().decode('utf-8')[-2000:])
                info = json.loads(line)
                self.admin.config['port'] = info['port']
                overview = self.admin.dispatch({'action': 'admin.overview'})
                self.assertEqual(overview['connectedPlayers'][0]['id'], info['playerId'])
                self.assertEqual(overview['accounts'][0]['accountHash'], info['accountHash'])
                before_balance = overview['accounts'][0]['balance']
                self.command('room.update', {'roomId': info['roomId'], 'capacity': 9, 'visibility': 'private'})
                self.assertTrue(self.command('room.close', {'roomId': info['roomId']})['closed'])
                self.command('maintenance.set', {'enabled': True})
                protocol = self.admin.protocol()
                self.assertTrue(protocol['ok'])
                self.assertTrue(protocol['maintenance'])
                self.command('maintenance.set', {'enabled': False})
                credited = self.command('eclats.adjust', {'accountHash': info['accountHash'], 'amount': 7, 'reason': 'Test local réel'})
                self.assertEqual(credited['balance'], before_balance + 7)
                with self.assertRaisesRegex(RuntimeError, 'INSUFFICIENT_BALANCE'):
                    self.command('eclats.adjust', {'accountHash': info['accountHash'], 'amount': -2147483647, 'reason': 'Doit être refusé'})
                refunded = self.command('casino.refund', {'accountHash': info['accountHash'], 'amount': 23, 'reason': 'Test local de jetons'})
                self.assertTrue(refunded['deliveryPending'])
                self.command('player.kick', {'playerId': info['playerId']})
                self.command('account.ban', {'accountHash': info['accountHash'], 'reason': 'Test local de bannissement', 'expiresAt': None})
                self.assertEqual(len(self.admin.admin_overview()['bans']), 1)
                self.command('account.unban', {'accountHash': info['accountHash'], 'reason': 'Test terminé'})
                self.assertEqual(len(self.admin.admin_overview()['bans']), 0)

                # The fixture is not a systemd service. Only its service membership
                # flag is replaced; PID, timestamps and reload checksums remain real.
                def local_monitor():
                    if not (self.data / 'monitor.json').exists():
                        return {}
                    raw = json.loads((self.data / 'monitor.json').read_text())
                    self.assertEqual(raw['pid'], info['pid'])
                    raw['processCurrent'] = True
                    return raw
                with patch.object(self.admin, 'monitor', side_effect=local_monitor):
                    current = self.admin.config_get()
                    changed = {**current['config'], 'prices': {**current['config']['prices'], 'single': 31}}
                    saved = self.admin.dispatch({'action': 'config.save', 'requestId': str(uuid.uuid4()), 'config': changed,
                                                 'expectedSha256': current['sha256'], 'reason': 'Prix de test local'})
                    self.assertTrue(saved['accepted'], saved)
                    history = self.admin.config_history()['items']
                    restored = self.admin.dispatch({'action': 'config.restore', 'requestId': str(uuid.uuid4()), 'version': history[-1]['id'],
                                                    'expectedSha256': saved['sha256'], 'reason': 'Rétablir le prix initial'})
                    self.assertTrue(restored['accepted'], restored)
                    self.assertEqual(json.loads(self.admin.config_path.read_bytes())['prices']['single'], 30)
                audit = self.admin.admin_audit()
                self.assertTrue(any(item['command'] == 'config.restore' and item['result']['ok'] for item in audit['items']))
                self.assertTrue(any(item['command'] == 'eclats.adjust' and not item['result']['ok'] for item in audit['items']))
                self.assertNotIn('A' * 43, json.dumps(audit))
                database_before = self.admin.database.read_bytes()
                stats = self.admin.statistics()
                self.assertEqual(stats['refundTotals']['coins'], 23)
                self.assertEqual(stats['casinoByGame'], [])
                self.assertEqual(stats['activityScope'], 'authenticatedConnections')
                self.assertEqual(stats['activePlayersByDay'][0]['players'], 1)
                self.assertEqual(self.admin.database.read_bytes(), database_before)
                self.assertEqual(identity.read_bytes(), identity_before)
            finally:
                if child.poll() is None:
                    with contextlib.suppress(BrokenPipeError):
                        child.stdin.write(b'close\n')
                        child.stdin.flush()
                    try:
                        child.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        child.kill()
                        child.wait()
                child.stdin.close()
                child.stdout.close()


if __name__ == '__main__':
    unittest.main()
