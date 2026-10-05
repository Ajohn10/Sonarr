import { ApiError } from 'Utilities/Fetch/fetchJson';
import fetchNdjson from 'Utilities/Fetch/fetchNdjson';
import type { Release } from './useReleases';

const UPDATE_INTERVAL_MS = 100;

interface ReleasesFrame {
  type: string;
  releases?: Release[];
  order?: string[];
  message?: string;
  indexer?: string;
}

const getReleaseKey = (release: Release) => release.release.guid;

// Streams /release/stream, upserting releases by guid and publishing partial results (throttled)
// until the complete frame provides the final ordering.
export default async function streamReleases(
  path: string,
  signal: AbortSignal,
  publish: (releases: Release[]) => void,
  onIndexerError: (message: string) => void
): Promise<Release[]> {
  const releases = new Map<string, Release>();
  const state = {
    completed: false,
    fatalError: undefined as string | undefined,
  };
  let lastPublish = 0;
  let timer: ReturnType<typeof setTimeout> | undefined = undefined;

  const flush = () => {
    timer = undefined;
    lastPublish = Date.now();
    publish(Array.from(releases.values()));
  };

  try {
    await fetchNdjson({
      path,
      signal,
      headers: {
        'X-Api-Key': window.Sonarr.apiKey,
        'X-Sonarr-Client': 'Sonarr',
      },
      onRecord: (record) => {
        const frame = record as ReleasesFrame;

        if (frame.type === 'releases') {
          frame.releases?.forEach((release) => {
            releases.set(getReleaseKey(release), release);
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
          state.completed = true;

          frame.order?.forEach((key, index) => {
            const release = releases.get(key);

            if (release) {
              release.releaseWeight = index;
            }
          });
        } else if (frame.type === 'error') {
          state.fatalError = frame.message ?? 'Search failed';
        }
      },
    });
  } finally {
    if (timer) {
      clearTimeout(timer);
    }
  }

  if (state.fatalError) {
    throw new ApiError(path, 500, 'Search failed', {
      message: state.fatalError,
      details: '',
    });
  }

  if (!state.completed) {
    throw new ApiError(path, 500, 'Search stream ended unexpectedly');
  }

  return Array.from(releases.values()).sort(
    (a, b) => a.releaseWeight - b.releaseWeight
  );
}
