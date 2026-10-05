import AppSectionState, {
  AppSectionFilterState,
} from 'App/State/AppSectionState';
import Release from 'typings/Release';

interface ReleasesAppState
  extends AppSectionState<Release>,
    AppSectionFilterState<Release> {
  indexerErrors: string[];
}

export default ReleasesAppState;
