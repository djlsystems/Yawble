import { describe, expect, it } from 'vitest';
import { encodeFrame, FrameDecoder, FrameKind, MAX_PAYLOAD } from '../../src/shared/frame';

describe('frame codec (mirror of core Ipc/Frame.cs)', () => {
  it('round-trips frames split across arbitrary chunks', () => {
    const a = encodeFrame(FrameKind.Json, Buffer.from('{"id":1}'));
    const b = encodeFrame(FrameKind.Pty, Buffer.from([0, 0, 0, 7, 0x1b]));
    const wire = Buffer.concat([a, b]);
    const decoder = new FrameDecoder();
    const frames = [...wire].flatMap((byte) => decoder.push(Buffer.from([byte])));
    expect(frames.map((f) => f.kind)).toEqual([FrameKind.Json, FrameKind.Pty]);
    expect(frames[0].payload.toString()).toBe('{"id":1}');
  });

  it('uses a big-endian length and a kind byte', () => {
    expect([...encodeFrame(FrameKind.Json, Buffer.from('ab'))]).toEqual([0, 0, 0, 2, 1, 97, 98]);
  });

  it('rejects oversized and unknown frames', () => {
    const big = Buffer.alloc(5);
    big.writeUInt32BE(MAX_PAYLOAD + 1, 0);
    big.writeUInt8(1, 4);
    expect(() => new FrameDecoder().push(big)).toThrow(/exceeds/);
    expect(() => new FrameDecoder().push(Buffer.from([0, 0, 0, 0, 9]))).toThrow(/unknown frame kind/);
  });
});
