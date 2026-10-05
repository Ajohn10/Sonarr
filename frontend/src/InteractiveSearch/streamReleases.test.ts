import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import streamReleases from './streamReleases';

const encoder = new TextEncoder();

function mockStream(chunks: string[], gate?: Promise<void>) {
  const body = new ReadableStream<Uint8Array>({
    async start(controller) {
      for (const [i, chunk] of chunks.entries()) {
        if (gate && i === chunks.length - 1) {
          await gate;
        }

        controller.enqueue(encoder.encode(chunk));
      }

      controller.close();
    },
  });
  return new Response(body, { status: 200 });
}

const rel = (guid: string, title = guid) => ({ release: { guid, title } });
const line = (o: unknown) => `${JSON.stringify(o)}\n`;

beforeEach(() => {});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('streamReleases', () => {
  it('accumulates batches, dedupes by guid and applies final order', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        mockStream([
          line({ type: 'releases', releases: [rel('a'), rel('b')] }),
          // split mid-line across chunks
          line({
            type: 'releases',
            releases: [rel('b', 'better'), rel('c')],
          }).slice(0, 20),
          line({
            type: 'releases',
            releases: [rel('b', 'better'), rel('c')],
          }).slice(20),
          line({ type: 'complete', order: ['c', 'a', 'b'] }),
        ])
      )
    );
    const result = await streamReleases(
      '/api/v5/release/stream',
      new AbortController().signal,
      () => undefined,
      () => undefined
    );
    expect(result.map((r) => r.release.guid).sort()).toEqual(['a', 'b', 'c']);
    expect(result.find((r) => r.release.guid === 'b')?.release.title).toBe(
      'better'
    );
    expect(result[0]).toBeDefined();
    const weights = Object.fromEntries(
      result.map((r) => [r.release.guid, r.releaseWeight])
    );
    expect(weights).toEqual({ c: 0, a: 1, b: 2 });
  });

  it('publishes partial results before completion (grabbable early)', async () => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((r) => (release = r));
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        mockStream(
          [
            line({ type: 'releases', releases: [rel('a')] }),
            line({ type: 'complete', order: ['a'] }),
          ],
          gate
        )
      )
    );
    const published: string[][] = [];
    const p = streamReleases(
      '/x',
      new AbortController().signal,
      (r) => published.push(r.map((x) => x.release.guid)),
      () => undefined
    );
    await vi.waitFor(() => expect(published.length).toBeGreaterThan(0));
    expect(published[0]).toEqual(['a']);
    release();
    await p;
  });

  it('reports indexer errors without failing', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        mockStream([
          line({ type: 'indexerError', indexer: 'X', message: 'boom' }),
          line({ type: 'complete', order: [] }),
        ])
      )
    );
    const errors: string[] = [];
    await streamReleases(
      '/x',
      new AbortController().signal,
      () => undefined,
      (m) => errors.push(m)
    );
    expect(errors).toEqual(['X: boom']);
  });

  it('throws on fatal error frame and on missing complete', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => mockStream([line({ type: 'error', message: 'bad' })]))
    );
    await expect(
      streamReleases(
        '/x',
        new AbortController().signal,
        () => undefined,
        () => undefined
      )
    ).rejects.toThrow();
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        mockStream([line({ type: 'releases', releases: [rel('a')] })])
      )
    );
    await expect(
      streamReleases(
        '/x',
        new AbortController().signal,
        () => undefined,
        () => undefined
      )
    ).rejects.toThrow();
  });

  it('propagates cancellation', async () => {
    const ac = new AbortController();
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_p: string, init: RequestInit) => {
        expect(init.signal).toBe(ac.signal);
        throw new DOMException('Aborted', 'AbortError');
      })
    );
    ac.abort();
    await expect(
      streamReleases(
        '/x',
        ac.signal,
        () => undefined,
        () => undefined
      )
    ).rejects.toThrow();
  });
});
