import Release from 'typings/Release';
import fetchNdjson from 'Utilities/Fetch/fetchNdjson';

export const UPDATE_INTERVAL_MS = 100;

interface ReleasesFrame {
  type: string;
  releases?: Release[];
  order?: string[];
  message?: string;
  indexer?: string;
}

export interface StreamReleasesOptions {
  url: string;
  apiKey: string;
  signal: AbortSignal;
  // Receives releases that arrived since the last call (already deduped by guid, throttled)
  onReleases: (releases: Release[]) => void;
  onIndexerError: (message: string) => void;
  // Receives the guids in their final order once the server has finished prioritizing
  onComplete: (order: string[]) => void;
}

// Streams /release/stream: batches are upserted by guid and published (throttled) as they arrive,
// the complete frame provides the final ordering. Rejects on a fatal error frame or a truncated stream.
async function streamReleases({
  url,
  apiKey,
  signal,
  onReleases,
  onIndexerError,
  onComplete,
}: StreamReleasesOptions): Promise<void> {
  let pending = new Map<string, Release>();
  let completed = false;
  let fatalError: string | undefined = undefined;
  let lastPublish = 0;
  let timer: ReturnType<typeof setTimeout> | undefined = undefined;

  const flush = () => {
    if (timer) {
      clearTimeout(timer);
      timer = undefined;
    }

    if (!pending.size) {
      return;
    }

    const releases = Array.from(pending.values());

    pending = new Map();
    lastPublish = Date.now();
    onReleases(releases);
  };

  try {
    await fetchNdjson({
      url,
      signal,
      headers: { 'X-Api-Key': apiKey },
      onRecord: (record) => {
        const frame = record as ReleasesFrame;

        if (frame.type === 'releases') {
          frame.releases?.forEach((release) => {
            pending.set(release.guid, release);
          });

          if (!timer) {
            timer = setTimeout(
              flush,
              Math.max(0, UPDATE_INTERVAL_MS - (Date.now() - lastPublish))
            );
          }
        } else if (frame.type === 'indexerError') {
          onIndexerError(
            frame.indexer
              ? `${frame.indexer}: ${frame.message ?? ''}`
              : frame.message ?? ''
          );
        } else if (frame.type === 'complete') {
          completed = true;
          flush();
          onComplete(frame.order ?? []);
        } else if (frame.type === 'error') {
          fatalError = frame.message ?? 'Search failed';
        }
      },
    });
  } finally {
    if (signal.aborted) {
      if (timer) {
        clearTimeout(timer);
      }
    } else {
      // Deliver what arrived even when the stream failed
      flush();
    }
  }

  if (fatalError) {
    throw new Error(fatalError);
  }

  if (!completed) {
    throw new Error('Search stream ended unexpectedly');
  }
}

export default streamReleases;
