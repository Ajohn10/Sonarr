import { afterEach, describe, expect, it, vi } from 'vitest';
import Release from 'typings/Release';
import { applyReleaseOrder, upsertReleases } from './releaseStreamState';
import streamReleases from './streamReleases';

const encoder = new TextEncoder();

const rel = (guid: string, title = guid) => ({ guid, title } as Release);
const line = (o: unknown) => `${JSON.stringify(o)}\n`;

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

const run = (
  signal = new AbortController().signal,
  hooks: {
    onReleases?: (r: Release[]) => void;
    onIndexerError?: (m: string) => void;
    onComplete?: (o: string[]) => void;
  } = {}
) =>
  streamReleases({
    url: '/api/v3/release/stream?episodeId=1',
    apiKey: 'k',
    signal,
    onReleases: hooks.onReleases ?? (() => undefined),
    onIndexerError: hooks.onIndexerError ?? (() => undefined),
    onComplete: hooks.onComplete ?? (() => undefined),
  });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('streamReleases', () => {
  it('sends the ndjson accept header and api key', async () => {
    const fetchMock = vi.fn(async () =>
      mockStream([line({ type: 'complete', order: [] })])
    );
    vi.stubGlobal('fetch', fetchMock);

    await run();

    const init = (fetchMock.mock.calls[0] as unknown[])[1] as RequestInit;

    expect(init.headers).toMatchObject({
      Accept: 'application/x-ndjson',
      'X-Api-Key': 'k',
    });
  });

  it('accumulates batches (even when a line is split across chunks) and dedupes by guid per publish', async () => {
    const second = line({
      type: 'releases',
      releases: [rel('b', 'better'), rel('c')],
    });

    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        mockStream([
          line({ type: 'releases', releases: [rel('a'), rel('b')] }),
          second.slice(0, 20),
          second.slice(20),
          line({ type: 'complete', order: ['c', 'a', 'b'] }),
        ])
      )
    );

    const published: Release[][] = [];
    let order: string[] = [];

    await run(undefined, {
      onReleases: (r) => published.push(r),
      onComplete: (o) => (order = o),
    });

    const all = published.flat();

    expect(all.map((r) => r.guid).sort()).toEqual(['a', 'b', 'c']);
    expect(all.filter((r) => r.guid === 'b').pop()?.title).toBe('better');
    expect(order).toEqual(['c', 'a', 'b']);
  });

  it('publishes partial results before completion', async () => {
    let open: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => (open = resolve));

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
    const completed = vi.fn();
    const promise = run(undefined, {
      onReleases: (r) => published.push(r.map((x) => x.guid)),
      onComplete: completed,
    });

    await vi.waitFor(() => expect(published).toEqual([['a']]));
    expect(completed).not.toHaveBeenCalled();

    open();
    await promise;

    expect(completed).toHaveBeenCalledWith(['a']);
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

    await run(undefined, { onIndexerError: (m) => errors.push(m) });

    expect(errors).toEqual(['X: boom']);
  });

  it('rejects on a fatal error frame, keeping already received releases', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        mockStream([
          line({ type: 'releases', releases: [rel('a')] }),
          line({ type: 'error', message: 'bad' }),
        ])
      )
    );

    const published: Release[][] = [];

    await expect(
      run(undefined, { onReleases: (r) => published.push(r) })
    ).rejects.toThrow('bad');
    expect(published.flat().map((r) => r.guid)).toEqual(['a']);
  });

  it('rejects when the stream ends without a complete frame', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        mockStream([line({ type: 'releases', releases: [rel('a')] })])
      )
    );

    await expect(run()).rejects.toThrow('ended unexpectedly');
  });

  it('rejects with the server message on a non-200 response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ message: 'nope' }), { status: 400 })
      )
    );

    await expect(run()).rejects.toThrow('nope');
  });

  it('passes the abort signal to fetch and does not publish after cancellation', async () => {
    const controller = new AbortController();
    const published = vi.fn();

    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init: RequestInit) => {
        expect(init.signal).toBe(controller.signal);

        const stream = new ReadableStream<Uint8Array>({
          start(c) {
            c.enqueue(
              encoder.encode(line({ type: 'releases', releases: [rel('a')] }))
            );
            c.error(new DOMException('Aborted', 'AbortError'));
          },
        });

        return new Response(stream, { status: 200 });
      })
    );

    const promise = run(controller.signal, { onReleases: published });

    controller.abort();

    await expect(promise).rejects.toThrow();
    await new Promise((resolve) => setTimeout(resolve, 150));
    expect(published).not.toHaveBeenCalled();
  });
});

describe('release state', () => {
  it('upserts by guid keeping position and grab state', () => {
    const first = upsertReleases([], [rel('a'), rel('b')]);

    first[0] = { ...first[0], isGrabbing: true };

    const next = upsertReleases(first, [rel('a', 'updated'), rel('c')]);

    expect(next.map((r) => r.guid)).toEqual(['a', 'b', 'c']);
    expect(next[0].title).toBe('updated');
    expect(next[0].isGrabbing).toBe(true);
    expect(next.map((r) => r.releaseWeight)).toEqual([0, 1, 2]);
  });

  it('applies the final order and keeps unknown releases last', () => {
    const items = upsertReleases([], [rel('a'), rel('b'), rel('c')]);
    const ordered = applyReleaseOrder(items, ['c', 'a']);
    const weights = Object.fromEntries(
      ordered.map((r) => [r.guid, r.releaseWeight])
    );

    expect(weights.c).toBe(0);
    expect(weights.a).toBe(1);
    expect(weights.b).toBeGreaterThan(1);
  });
});
