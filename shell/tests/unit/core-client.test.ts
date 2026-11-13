import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { mkdtempSync } from 'node:fs';
import { afterEach, describe, expect, it } from 'vitest';
import { CoreClient, CoreRpcError } from '../../src/main/core-client';
import { encodeFrame, FrameDecoder, FrameKind } from '../../src/shared/frame';

// A stand-in core that speaks the same protocol, so the client is tested without .NET.
function fakeCore(token: string): Promise<{ address: string; server: net.Server }> {
  const address = process.platform === 'win32'
    ? `\\\\.\\pipe\\yawble-test-${process.pid}-${Date.now()}`
    : path.join(mkdtempSync(path.join(os.tmpdir(), 'yawble-')), 'core.sock');
  const server = net.createServer((socket) => {
    const decoder = new FrameDecoder();
    let authed = false;
    socket.on('data', (chunk) => {
      for (const frame of decoder.push(chunk)) {
        const req = JSON.parse(frame.payload.toString());
        let reply: object;
        if (!authed) {
          authed = req.method === 'auth' && req.params?.token === token;
          reply = authed ? { id: req.id, result: { authenticated: true } } : { id: req.id, error: { code: 'unauthorized', message: 'no' } };
        } else if (req.method === 'push.me') {
          // A push has no id; it arrives before the answer.
          socket.write(encodeFrame(FrameKind.Json, Buffer.from(JSON.stringify({ event: 'terminal.output', data: { channel: 'member:1' } }))));
          reply = { id: req.id, result: true };
        } else if (req.method === 'sandbox.getAvailableBackends') {
          reply = { id: req.id, result: [] };
        } else {
          reply = { id: req.id, error: { code: 'unknown_method', message: req.method } };
        }
        socket.write(encodeFrame(FrameKind.Json, Buffer.from(JSON.stringify(reply))));
        if (!authed) socket.end();
      }
    });
  });
  return new Promise((resolve) => server.listen(address, () => resolve({ address, server })));
}

let server: net.Server | undefined;
afterEach(() => server?.close());

describe('CoreClient', () => {
  it('authenticates and calls methods by id', async () => {
    const core = await fakeCore('secret');
    server = core.server;
    const client = await CoreClient.connect(core.address, 'secret');
    await expect(client.call('sandbox.getAvailableBackends')).resolves.toEqual([]);
    await expect(client.call('nope')).rejects.toBeInstanceOf(CoreRpcError);
    client.close();
  });

  it('hands pushes (frames with no id) to its listeners', async () => {
    const core = await fakeCore('secret');
    server = core.server;
    const client = await CoreClient.connect(core.address, 'secret');
    const seen: unknown[] = [];
    const off = client.onEvent((e) => seen.push(e));
    await expect(client.call('push.me')).resolves.toBe(true);
    expect(seen).toEqual([{ event: 'terminal.output', data: { channel: 'member:1' } }]);
    off();
    await client.call('push.me');
    expect(seen).toHaveLength(1);
    client.close();
  });

  it('fails to connect with the wrong token', async () => {
    const core = await fakeCore('secret');
    server = core.server;
    await expect(CoreClient.connect(core.address, 'wrong')).rejects.toThrow(/no/);
  });
});
