import { createAction } from 'redux-actions';
import { filterBuilderTypes, filterBuilderValueTypes, filterTypePredicates, filterTypes, sortDirections } from 'Helpers/Props';
import { applyReleaseOrder, upsertReleases as upsertReleaseItems } from 'InteractiveSearch/releaseStreamState';
import streamReleases from 'InteractiveSearch/streamReleases';
import { createThunk, handleThunks } from 'Store/thunks';
import sortByProp from 'Utilities/Array/sortByProp';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import { set } from './baseActions';
import createHandleActions from './Creators/createHandleActions';
import createSetClientSideCollectionFilterReducer from './Creators/Reducers/createSetClientSideCollectionFilterReducer';
import createSetClientSideCollectionSortReducer from './Creators/Reducers/createSetClientSideCollectionSortReducer';

//
// Variables

export const section = 'releases';
export const episodeSection = 'releases.episode';
export const seasonSection = 'releases.season';

let abortCurrentRequest = null;
let fetchId = 0;

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  error: null,
  items: [],
  indexerErrors: [],
  sortKey: 'releaseWeight',
  sortDirection: sortDirections.ASCENDING,
  sortPredicates: {
    age: function(item, direction) {
      return item.ageMinutes;
    },

    peers: function(item, direction) {
      const seeders = item.seeders || 0;
      const leechers = item.leechers || 0;

      return seeders * 1000000 + leechers;
    },

    languages: function(item, direction) {
      if (item.languages.length > 1) {
        return 10000;
      }

      return item.languages[0]?.id ?? 0;
    },

    rejections: function(item, direction) {
      const rejections = item.rejections;
      const releaseWeight = item.releaseWeight;

      if (rejections.length !== 0) {
        return releaseWeight + 1000000;
      }

      return releaseWeight;
    }
  },

  filters: [
    {
      key: 'all',
      label: () => translate('All'),
      filters: []
    },
    {
      key: 'season-pack',
      label: () => translate('SeasonPack'),
      filters: [
        {
          key: 'fullSeason',
          value: true,
          type: filterTypes.EQUAL
        }
      ]
    },
    {
      key: 'not-season-pack',
      label: () => translate('NotSeasonPack'),
      filters: [
        {
          key: 'fullSeason',
          value: false,
          type: filterTypes.EQUAL
        }
      ]
    }
  ],

  filterPredicates: {
    quality: function(item, value, type) {
      const qualityId = item.quality.quality.id;

      if (type === filterTypes.EQUAL) {
        return qualityId === value;
      }

      if (type === filterTypes.NOT_EQUAL) {
        return qualityId !== value;
      }

      // Default to false
      return false;
    },

    languages: function(item, filterValue, type) {
      const predicate = filterTypePredicates[type];

      const languages = item.languages.map((language) => language.name);

      return predicate(languages, filterValue);
    },

    rejectionCount: function(item, value, type) {
      const rejectionCount = item.rejections.length;

      switch (type) {
        case filterTypes.EQUAL:
          return rejectionCount === value;

        case filterTypes.GREATER_THAN:
          return rejectionCount > value;

        case filterTypes.GREATER_THAN_OR_EQUAL:
          return rejectionCount >= value;

        case filterTypes.LESS_THAN:
          return rejectionCount < value;

        case filterTypes.LESS_THAN_OR_EQUAL:
          return rejectionCount <= value;

        case filterTypes.NOT_EQUAL:
          return rejectionCount !== value;

        default:
          return false;
      }
    },

    peers: function(item, value, type) {
      const seeders = item.seeders || 0;
      const leechers = item.leechers || 0;
      const peers = seeders + leechers;

      switch (type) {
        case filterTypes.EQUAL:
          return peers === value;

        case filterTypes.GREATER_THAN:
          return peers > value;

        case filterTypes.GREATER_THAN_OR_EQUAL:
          return peers >= value;

        case filterTypes.LESS_THAN:
          return peers < value;

        case filterTypes.LESS_THAN_OR_EQUAL:
          return peers <= value;

        case filterTypes.NOT_EQUAL:
          return peers !== value;

        default:
          return false;
      }
    }
  },

  filterBuilderProps: [
    {
      name: 'title',
      label: () => translate('Title'),
      type: filterBuilderTypes.STRING
    },
    {
      name: 'age',
      label: () => translate('Age'),
      type: filterBuilderTypes.NUMBER
    },
    {
      name: 'protocol',
      label: () => translate('Protocol'),
      type: filterBuilderTypes.EXACT,
      valueType: filterBuilderValueTypes.PROTOCOL
    },
    {
      name: 'indexerId',
      label: () => translate('Indexer'),
      type: filterBuilderTypes.EXACT,
      valueType: filterBuilderValueTypes.INDEXER
    },
    {
      name: 'size',
      label: () => translate('Size'),
      type: filterBuilderTypes.NUMBER,
      valueType: filterBuilderValueTypes.BYTES
    },
    {
      name: 'seeders',
      label: () => translate('Seeders'),
      type: filterBuilderTypes.NUMBER
    },
    {
      name: 'peers',
      label: () => translate('Peers'),
      type: filterBuilderTypes.NUMBER
    },
    {
      name: 'quality',
      label: () => translate('Quality'),
      type: filterBuilderTypes.EXACT,
      valueType: filterBuilderValueTypes.QUALITY
    },
    {
      name: 'languages',
      label: () => translate('Languages'),
      type: filterBuilderTypes.ARRAY,
      optionsSelector: function(items) {
        const genreList = items.reduce((acc, release) => {
          release.languages.forEach((language) => {
            acc.push({
              id: language.name,
              name: language.name
            });
          });

          return acc;
        }, []);

        return genreList.sort(sortByProp('name'));
      }
    },
    {
      name: 'customFormatScore',
      label: () => translate('CustomFormatScore'),
      type: filterBuilderTypes.NUMBER
    },
    {
      name: 'rejectionCount',
      label: () => translate('RejectionCount'),
      type: filterBuilderTypes.NUMBER
    },
    {
      name: 'fullSeason',
      label: () => translate('SeasonPack'),
      type: filterBuilderTypes.EXACT,
      valueType: filterBuilderValueTypes.BOOL
    },
    {
      name: 'episodeRequested',
      label: () => translate('EpisodeRequested'),
      type: filterBuilderTypes.EXACT,
      valueType: filterBuilderValueTypes.BOOL
    }
  ],

  episode: {
    selectedFilterKey: 'all'
  },

  season: {
    selectedFilterKey: 'season-pack'
  }
};

export const persistState = [
  'releases.episode.selectedFilterKey',
  'releases.episode.customFilters',
  'releases.season.selectedFilterKey',
  'releases.season.customFilters'
];

//
// Actions Types

export const FETCH_RELEASES = 'releases/fetchReleases';
export const CANCEL_FETCH_RELEASES = 'releases/cancelFetchReleases';
export const SET_RELEASES_SORT = 'releases/setReleasesSort';
export const CLEAR_RELEASES = 'releases/clearReleases';
export const UPSERT_RELEASES = 'releases/upsertReleases';
export const COMPLETE_RELEASES = 'releases/completeReleases';
export const ADD_RELEASE_INDEXER_ERROR = 'releases/addReleaseIndexerError';
export const GRAB_RELEASE = 'releases/grabRelease';
export const UPDATE_RELEASE = 'releases/updateRelease';
export const SET_EPISODE_RELEASES_FILTER = 'releases/setEpisodeReleasesFilter';
export const SET_SEASON_RELEASES_FILTER = 'releases/setSeasonReleasesFilter';

//
// Action Creators

export const fetchReleases = createThunk(FETCH_RELEASES);
export const cancelFetchReleases = createThunk(CANCEL_FETCH_RELEASES);
export const setReleasesSort = createAction(SET_RELEASES_SORT);
export const clearReleases = createAction(CLEAR_RELEASES);
export const upsertReleases = createAction(UPSERT_RELEASES);
export const completeReleases = createAction(COMPLETE_RELEASES);
export const addReleaseIndexerError = createAction(ADD_RELEASE_INDEXER_ERROR);
export const grabRelease = createThunk(GRAB_RELEASE);
export const updateRelease = createAction(UPDATE_RELEASE);
export const setEpisodeReleasesFilter = createAction(SET_EPISODE_RELEASES_FILTER);
export const setSeasonReleasesFilter = createAction(SET_SEASON_RELEASES_FILTER);

//
// Helpers

// Streams the search so releases show up (and can be grabbed) while indexers are still responding
function fetchReleasesHelper(getState, payload, dispatch) {
  const id = ++fetchId;
  const controller = new AbortController();
  const params = new URLSearchParams();

  Object.keys(payload).forEach((key) => {
    if (payload[key] != null) {
      params.append(key, payload[key]);
    }
  });

  dispatch(set({
    section,
    isFetching: true,
    isPopulated: false,
    error: null,
    items: [],
    indexerErrors: []
  }));

  const isCurrent = () => id === fetchId && !controller.signal.aborted;

  streamReleases({
    url: `${window.Sonarr.apiRoot}/release/stream?${params.toString()}`,
    apiKey: window.Sonarr.apiKey,
    signal: controller.signal,
    onReleases: (releases) => {
      if (isCurrent()) {
        dispatch(upsertReleases({ releases }));
      }
    },
    onIndexerError: (message) => {
      if (isCurrent()) {
        dispatch(addReleaseIndexerError({ message }));
      }
    },
    onComplete: (order) => {
      if (isCurrent()) {
        dispatch(completeReleases({ order }));
      }
    }
  }).catch((error) => {
    if (isCurrent()) {
      dispatch(set({
        section,
        isFetching: false,
        error: {
          status: error.status ?? 500,
          statusText: error.message,
          responseJSON: { message: error.message }
        }
      }));
    }
  });

  return () => controller.abort();
}

//
// Action Handlers

export const actionHandlers = handleThunks({

  [FETCH_RELEASES]: function(getState, payload, dispatch) {
    const abortRequest = fetchReleasesHelper(getState, payload, dispatch);

    abortCurrentRequest = abortRequest;
  },

  [CANCEL_FETCH_RELEASES]: function(getState, payload, dispatch) {
    if (abortCurrentRequest) {
      abortCurrentRequest();
      abortCurrentRequest = null;
      fetchId++;
      dispatch(set({ section, isFetching: false }));
    }
  },

  [GRAB_RELEASE]: function(getState, payload, dispatch) {
    const guid = payload.guid;

    dispatch(updateRelease({ guid, isGrabbing: true }));

    const promise = createAjaxRequest({
      url: '/release',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify(payload)
    }).request;

    promise.done((data) => {
      dispatch(updateRelease({
        guid,
        isGrabbing: false,
        isGrabbed: true,
        grabError: null
      }));
    });

    promise.fail((xhr) => {
      const grabError = xhr.responseJSON && xhr.responseJSON.message || 'Failed to add to download queue';

      dispatch(updateRelease({
        guid,
        isGrabbing: false,
        isGrabbed: false,
        grabError
      }));
    });
  }
});

//
// Reducers

export const reducers = createHandleActions({

  [CLEAR_RELEASES]: (state) => {
    const {
      episode,
      season,
      ...otherDefaultState
    } = defaultState;

    return Object.assign({}, state, otherDefaultState);
  },

  [UPSERT_RELEASES]: (state, { payload }) => {
    const items = upsertReleaseItems(state.items, payload.releases);

    return Object.assign({}, state, { items, isPopulated: true });
  },

  [COMPLETE_RELEASES]: (state, { payload }) => {
    const items = applyReleaseOrder(state.items, payload.order);

    return Object.assign({}, state, { items, isFetching: false, isPopulated: true, error: null });
  },

  [ADD_RELEASE_INDEXER_ERROR]: (state, { payload }) => {
    return Object.assign({}, state, { indexerErrors: [...state.indexerErrors, payload.message] });
  },

  [UPDATE_RELEASE]: (state, { payload }) => {
    const guid = payload.guid;
    const newState = Object.assign({}, state);
    const items = newState.items;
    const index = items.findIndex((item) => item.guid === guid);

    // Don't try to update if there isnt a matching item (the user closed the modal)

    if (index >= 0) {
      const item = Object.assign({}, items[index], payload);

      newState.items = [...items];
      newState.items.splice(index, 1, item);
    }

    return newState;
  },

  [SET_RELEASES_SORT]: createSetClientSideCollectionSortReducer(section),
  [SET_EPISODE_RELEASES_FILTER]: createSetClientSideCollectionFilterReducer(episodeSection),
  [SET_SEASON_RELEASES_FILTER]: createSetClientSideCollectionFilterReducer(seasonSection)

}, defaultState, section);
