using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class ReleaseSearchStreamingFixture : CoreTest<ReleaseSearchService>
    {
        private Mock<IIndexer> _indexer;
        private Series _series;
        private RecordingSink _sink;

        [SetUp]
        public void SetUp()
        {
            _indexer = Mocker.GetMock<IIndexer>();
            _indexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 1, Name = "Indexer" });
            _indexer.SetupGet(s => s.SupportsSearch).Returns(true);

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(new List<IIndexer> { _indexer.Object });

            _series = Builder<Series>.CreateNew().With(v => v.Monitored = true).With(v => v.SeriesType = SeriesTypes.Standard).Build();

            var episode = Builder<Episode>.CreateNew()
                .With(v => v.SeriesId = _series.Id)
                .With(v => v.Series = _series)
                .With(v => v.SeasonNumber = 1)
                .With(v => v.EpisodeNumber = 1)
                .With(v => v.Monitored = true)
                .With(v => v.AirDate = "2020-01-01")
                .With(v => v.AirDateUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .Build();

            Mocker.GetMock<ISeriesService>().Setup(v => v.GetSeries(_series.Id)).Returns(_series);
            Mocker.GetMock<IEpisodeService>().Setup(v => v.GetEpisodesBySeason(_series.Id, 1)).Returns(new List<Episode> { episode });

            Mocker.GetMock<ISceneMappingService>().Setup(s => s.FindByTvdbId(It.IsAny<int>())).Returns(new List<SceneMapping>());
            Mocker.GetMock<ISceneMappingService>()
                  .Setup(s => s.GetSceneNames(It.IsAny<int>(), It.IsAny<List<int>>(), It.IsAny<List<int>>()))
                  .Returns(new List<string>());

            // One decision per release; releases titled "bad" carry a rejection
            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                .Returns<List<ReleaseInfo>, SearchCriteriaBase>((releases, _) => releases
                    .Select(r => r.Title == "bad"
                        ? new DownloadDecision(new RemoteEpisode { Release = r }, new DownloadRejection(DownloadRejectionReason.Unknown, "bad"))
                        : new DownloadDecision(new RemoteEpisode { Release = r }))
                    .ToList());

            _sink = new RecordingSink();
        }

        private static void SetupFetch(Mock<IIndexer> indexer, Func<Task<IList<ReleaseInfo>>> fetch)
        {
            indexer.Setup(v => v.Fetch(It.IsAny<SeasonSearchCriteria>())).Returns(fetch);
            indexer.Setup(v => v.Fetch(It.IsAny<SingleEpisodeSearchCriteria>())).Returns(fetch);
        }

        private static ReleaseInfo Release(string guid, string title = "good")
        {
            return new ReleaseInfo { Guid = guid, Title = title, IndexerId = 1 };
        }

        private Task<List<DownloadDecision>> Search(CancellationToken token = default)
        {
            return Subject.SeasonSearch(_series.Id, 1, false, false, true, false, _sink, token);
        }

        [Test]
        public async Task should_emit_pages_before_the_indexer_finishes()
        {
            var finish = new TaskCompletionSource();
            var firstPageEmitted = new TaskCompletionSource();

            _sink.OnEmit = () => firstPageEmitted.TrySetResult();

            SetupFetch(_indexer, async () =>
                {
                    await ReleaseSearchScope.Current.PublishAsync(new[] { Release("a") });
                    await finish.Task;

                    return (IList<ReleaseInfo>)new List<ReleaseInfo>();
                });

            var search = Search();

            await firstPageEmitted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            search.IsCompleted.Should().BeFalse();
            _sink.Emitted.Select(d => d.RemoteEpisode.Release.Guid).Should().BeEquivalentTo(new[] { "a" });

            finish.SetResult();
            await search;
        }

        [Test]
        public async Task should_upsert_duplicate_guids_and_only_emit_improvements()
        {
            SetupFetch(_indexer, async () =>
                {
                    await ReleaseSearchScope.Current.PublishAsync(new[] { Release("a", "bad") });
                    await ReleaseSearchScope.Current.PublishAsync(new[] { Release("a", "good") });
                    await ReleaseSearchScope.Current.PublishAsync(new[] { Release("a", "bad") });

                    return (IList<ReleaseInfo>)new List<ReleaseInfo>();
                });

            await Search();

            _sink.Emitted.Should().HaveCount(2);
            _sink.Emitted.Last().Approved.Should().BeTrue();
        }

        [Test]
        public async Task should_not_publish_pages_twice_when_the_indexer_also_returns_them()
        {
            var page = Release("a");

            SetupFetch(_indexer, async () =>
                {
                    await ReleaseSearchScope.Current.PublishAsync(new[] { page });

                    return (IList<ReleaseInfo>)new List<ReleaseInfo> { page, Release("b") };
                });

            await Search();

            _sink.Emitted.Select(d => d.RemoteEpisode.Release.Guid).Should().BeEquivalentTo(new[] { "a", "b" });
        }

        [Test]
        public async Task should_report_indexer_errors_to_the_sink_and_keep_other_results()
        {
            var failing = new Mock<IIndexer>();
            failing.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 2, Name = "Failing" });
            failing.SetupGet(s => s.SupportsSearch).Returns(true);
            SetupFetch(failing, () => throw new InvalidOperationException("boom"));

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(new List<IIndexer> { _indexer.Object, failing.Object });

            SetupFetch(_indexer, () => Task.FromResult((IList<ReleaseInfo>)new List<ReleaseInfo> { Release("a") }));

            var result = await Search();

            result.Should().HaveCount(1);
            _sink.Errors.Should().ContainSingle().Which.Should().Be((2, "Failing", "boom"));
        }

        [Test]
        public async Task should_cancel_the_indexer_search()
        {
            using var cts = new CancellationTokenSource();
            var started = new TaskCompletionSource();
            CancellationToken seen = default;

            SetupFetch(_indexer, async () =>
                {
                    seen = ReleaseSearchScope.Current.Token;
                    started.SetResult();
                    await Task.Delay(Timeout.Infinite, seen);

                    return (IList<ReleaseInfo>)new List<ReleaseInfo>();
                });

            var search = Search(cts.Token);

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();

            Func<Task> act = async () => await search;

            await act.Should().ThrowAsync<OperationCanceledException>();
            seen.IsCancellationRequested.Should().BeTrue();
            _sink.Errors.Should().BeEmpty();
        }

        private class RecordingSink : IReleaseSearchSink
        {
            public List<DownloadDecision> Emitted { get; } = new List<DownloadDecision>();
            public List<(int, string, string)> Errors { get; } = new List<(int, string, string)>();
            public Action OnEmit { get; set; }

            public Task OnDecisions(IReadOnlyList<DownloadDecision> decisions)
            {
                Emitted.AddRange(decisions);
                OnEmit?.Invoke();

                return Task.CompletedTask;
            }

            public Task OnIndexerError(int indexerId, string indexerName, string message)
            {
                Errors.Add((indexerId, indexerName, message));

                return Task.CompletedTask;
            }
        }
    }
}
