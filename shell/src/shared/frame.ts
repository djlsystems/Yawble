// The shell side of core/src/Yawble.Core/Ipc/Frame.cs:
// [u32 big-endian payload length][u8 kind][payload]. Kind 1 = JSON, kind 2 = PTY bytes.
export const FrameKind = { Json: 1, Pty: 2 } as const;
export type FrameKindValue = (typeof FrameKind)[keyof typeof FrameKind];
export const HEADER_SIZE = 5;
export const MAX_PAYLOAD = 16 * 1024 * 1024;

export interface Frame {
  kind: FrameKindValue;
  payload: Buffer;
}

export function encodeFrame(kind: FrameKindValue, payload: Buffer): Buffer {
  if (payload.length > MAX_PAYLOAD) throw new Error('frame too large');
  const header = Buffer.alloc(HEADER_SIZE);
  header.writeUInt32BE(payload.length, 0);
  header.writeUInt8(kind, 4);
  return Buffer.concat([header, payload]);
}

/** Accumulates stream chunks and yields whole frames. */
export class FrameDecoder {
  private buffer: Buffer = Buffer.alloc(0);

  push(chunk: Buffer): Frame[] {
    this.buffer = this.buffer.length ? Buffer.concat([this.buffer, chunk]) : chunk;
    const frames: Frame[] = [];
    while (this.buffer.length >= HEADER_SIZE) {
      const length = this.buffer.readUInt32BE(0);
      if (length > MAX_PAYLOAD) throw new Error(`frame of ${length} bytes exceeds the limit`);
      const kind = this.buffer.readUInt8(4);
      if (kind !== FrameKind.Json && kind !== FrameKind.Pty) throw new Error(`unknown frame kind ${kind}`);
      if (this.buffer.length < HEADER_SIZE + length) break;
      frames.push({ kind, payload: this.buffer.subarray(HEADER_SIZE, HEADER_SIZE + length) });
      this.buffer = this.buffer.subarray(HEADER_SIZE + length);
    }
    return frames;
  }
}
