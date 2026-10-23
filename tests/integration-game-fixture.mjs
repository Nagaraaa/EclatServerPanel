// Optional cross-repository check, launched by test_admin_integration.py.
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
const [build, directory] = process.argv.slice(2);
const { startServer } = await import(pathToFileURL(join(build, 'server/src/server.js')));
const { connect } = await import(pathToFileURL(join(build, 'tests/client.js')));
const server = await startServer({ database: join(directory, 'coop.sqlite'), port: 0, observabilityDirectory: directory,
  adminSocketPath: join(directory, 'admin.sock') });
const client = await connect(server.port);
const key = 'A'.repeat(43);
const hello = await client.request('hello', { name: 'LocalTest', account: key });
await client.request('eclats.update', { action: 'progress', otId: 1234, badges: 1, dex: 12, zone: 0 });
const room = (await client.request('room.create', { visibility: 'public' })).payload.room;
process.stdout.write(JSON.stringify({ pid: process.pid, port: server.port, playerId: hello.payload.playerId,
  accountHash: createHash('sha256').update(key).digest('hex'), roomId: room.id }) + '\n');
let closing = false;
process.stdin.on('data', async () => {
  if (closing) return;
  closing = true;
  await client.close();
  await server.close();
  process.stdin.destroy();
});
