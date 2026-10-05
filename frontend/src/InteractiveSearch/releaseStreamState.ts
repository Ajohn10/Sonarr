import Release from 'typings/Release';

// Merges streamed releases into the existing list keyed by guid in O(n), keeping the position
// and any client-side grab state of releases that were already shown.
export function upsertReleases(items: Release[], releases: Release[]) {
  const result = [...items];
  const indexByGuid = new Map(result.map((item, index) => [item.guid, index]));

  releases.forEach((release) => {
    const index = indexByGuid.get(release.guid);

    if (index === undefined) {
      indexByGuid.set(release.guid, result.length);

      // Arrival order until the final ordering is known
      result.push({ ...release, releaseWeight: result.length });
    } else {
      const { releaseWeight, isGrabbing, isGrabbed, grabError } = result[index];

      result[index] = {
        ...release,
        releaseWeight,
        isGrabbing,
        isGrabbed,
        grabError,
      };
    }
  });

  return result;
}

export function applyReleaseOrder(items: Release[], order: string[]) {
  const weights = new Map(order.map((guid, index) => [guid, index]));

  return items.map((item) => ({
    ...item,
    releaseWeight: weights.get(item.guid) ?? order.length + item.releaseWeight,
  }));
}
