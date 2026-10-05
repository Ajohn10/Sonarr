export class NdjsonHttpError extends Error {
  status: number;
  body?: { message?: string };

  constructor(status: number, statusText: string, body?: { message?: string }) {
    super(body?.message || statusText || `HTTP ${status}`);
    this.name = 'NdjsonHttpError';
    this.status = status;
    this.body = body;
  }
}

export interface FetchNdjsonOptions {
  url: string;
  headers?: Record<string, string>;
  signal?: AbortSignal;
  onRecord: (record: unknown) => void;
}

// Reads an application/x-ndjson response and invokes onRecord for each complete line as it arrives.
async function fetchNdjson({
  url,
  headers,
  signal,
  onRecord,
}: FetchNdjsonOptions): Promise<void> {
  const response = await fetch(url, {
    headers: {
      ...headers,
      Accept: 'application/x-ndjson',
    },
    signal,
  });

  if (!response.ok) {
    let body: { message?: string } | undefined = undefined;

    try {
      body = (await response.json()) as { message?: string };
    } catch {
      // The status is enough
    }

    throw new NdjsonHttpError(response.status, response.statusText, body);
  }

  if (!response.body) {
    throw new NdjsonHttpError(response.status, 'Response has no body');
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  const flushLine = (line: string) => {
    const trimmed = line.trim();

    if (!trimmed) {
      return;
    }

    try {
      onRecord(JSON.parse(trimmed));
    } catch (error) {
      if (!(error instanceof SyntaxError)) {
        throw error;
      }
    }
  };

  try {
    for (;;) {
      const { done, value } = await reader.read();

      if (done) {
        break;
      }

      buffer += decoder.decode(value, { stream: true });

      let newline = buffer.indexOf('\n');

      while (newline >= 0) {
        flushLine(buffer.slice(0, newline));
        buffer = buffer.slice(newline + 1);
        newline = buffer.indexOf('\n');
      }
    }

    buffer += decoder.decode();
    flushLine(buffer);
  } finally {
    reader.cancel().catch(() => undefined);
  }
}

export default fetchNdjson;
