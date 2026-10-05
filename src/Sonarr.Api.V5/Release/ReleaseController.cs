using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Validation;
using Sonarr.Http;
using Sonarr.Http.REST;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace Sonarr.Api.V5.Release;

[V5ApiController]
public class ReleaseController : RestController<ReleaseResource>
{
    private readonly IFetchAndParseRss _rssFetcherAndParser;
    private readonly ISearchForReleases _releaseSearchService;
    private readonly IMakeDownloadDecision _downloadDecisionMaker;
    private readonly IPrioritizeDownloadDecision _prioritizeDownloadDecision;
    private readonly IDownloadService _downloadService;
    private readonly ISeriesService _seriesService;
    private readonly IEpisodeService _episodeService;
    private readonly IParsingService _parsingService;
    private readonly IHistoryService _historyService;
    private readonly Logger _logger;

    private readonly QualityProfile _qualityProfile;
    private readonly ICached<RemoteEpisode> _remoteEpisodeCache;

    public ReleaseController(IFetchAndParseRss rssFetcherAndParser,
                         ISearchForReleases releaseSearchService,
                         IMakeDownloadDecision downloadDecisionMaker,
                         IPrioritizeDownloadDecision prioritizeDownloadDecision,
                         IDownloadService downloadService,
                         ISeriesService seriesService,
                         IEpisodeService episodeService,
                         IParsingService parsingService,
                         IHistoryService historyService,
                         ICacheManager cacheManager,
                         IQualityProfileService qualityProfileService,
                         Logger logger)
    {
        _rssFetcherAndParser = rssFetcherAndParser;
        _releaseSearchService = releaseSearchService;
        _downloadDecisionMaker = downloadDecisionMaker;
        _prioritizeDownloadDecision = prioritizeDownloadDecision;
        _downloadService = downloadService;
        _seriesService = seriesService;
        _episodeService = episodeService;
        _parsingService = parsingService;
        _historyService = historyService;
        _logger = logger;

        _qualityProfile = qualityProfileService.GetDefaultProfile(string.Empty);
        _remoteEpisodeCache = cacheManager.GetCache<RemoteEpisode>(GetType(), "remoteEpisodes");

        PostValidator.RuleFor(s => s.Release).NotNull();
        PostValidator.RuleFor(s => s.Release!.IndexerId).ValidId();
        PostValidator.RuleFor(s => s.Release!.Guid).NotEmpty();
    }

    [NonAction]
    public override Results<Ok<ReleaseResource>, NotFound> GetResourceByIdWithErrorHandler(int id)
    {
        return base.GetResourceByIdWithErrorHandler(id);
    }

    protected override ReleaseResource GetResourceById(int id)
    {
        throw new NotImplementedException();
    }

    [HttpPost]
    [Consumes("application/json")]
    public async Task<Results<Ok<ReleaseGrabResource>, NotFound>> DownloadRelease([FromBody] ReleaseGrabResource release)
    {
        var remoteEpisode = _remoteEpisodeCache.Find(GetCacheKey(release));

        if (remoteEpisode == null)
        {
            _logger.Debug("Couldn't find requested release in cache, cache timeout probably expired.");

            throw new NzbDroneClientException(HttpStatusCode.NotFound, "Couldn't find requested release in cache, try searching again");
        }

        try
        {
            if (release.Override != null)
            {
                var overrideInfo = release.Override;

                Ensure.That(overrideInfo.SeriesId, () => release.Override.SeriesId).IsNotNull();
                Ensure.That(overrideInfo.EpisodeIds, () => overrideInfo.EpisodeIds).IsNotNull();
                Ensure.That(overrideInfo.EpisodeIds, () => overrideInfo.EpisodeIds).HasItems();
                Ensure.That(overrideInfo.Quality, () => overrideInfo.Quality).IsNotNull();
                Ensure.That(overrideInfo.Languages, () => overrideInfo.Languages).IsNotNull();

                // Clone the remote episode so we don't overwrite anything on the original
                remoteEpisode = new RemoteEpisode
                {
                    Release = remoteEpisode.Release,
                    ParsedEpisodeInfo = remoteEpisode.ParsedEpisodeInfo.JsonClone(),
                    SceneMapping = remoteEpisode.SceneMapping,
                    MappedSeasonNumber = remoteEpisode.MappedSeasonNumber,
                    EpisodeRequested = remoteEpisode.EpisodeRequested,
                    DownloadAllowed = remoteEpisode.DownloadAllowed,
                    SeedConfiguration = remoteEpisode.SeedConfiguration,
                    CustomFormats = remoteEpisode.CustomFormats,
                    CustomFormatScore = remoteEpisode.CustomFormatScore,
                    SeriesMatchType = remoteEpisode.SeriesMatchType,
                    ReleaseSource = remoteEpisode.ReleaseSource
                };

                remoteEpisode.Series = _seriesService.GetSeries(overrideInfo.SeriesId!.Value);
                remoteEpisode.Episodes = _episodeService.GetEpisodes(overrideInfo.EpisodeIds);
                remoteEpisode.ParsedEpisodeInfo.Quality = overrideInfo.Quality;
                remoteEpisode.Languages = overrideInfo.Languages;
            }

            if (remoteEpisode.Series == null)
            {
                if (release.SearchInfo?.EpisodeId.HasValue == true)
                {
                    var episode = _episodeService.GetEpisode(release.SearchInfo.EpisodeId.Value);

                    remoteEpisode.Series = _seriesService.GetSeries(episode.SeriesId);
                    remoteEpisode.Episodes = new List<Episode> { episode };
                }
                else if (release.SearchInfo?.SeriesId.HasValue == true)
                {
                    var series = _seriesService.GetSeries(release.SearchInfo.SeriesId.Value);
                    var episodes = _parsingService.GetEpisodes(remoteEpisode.ParsedEpisodeInfo, series, true);

                    if (episodes.Empty())
                    {
                        throw new NzbDroneClientException(HttpStatusCode.NotFound, "Unable to parse episodes in the release, will need to be manually provided");
                    }

                    remoteEpisode.Series = series;
                    remoteEpisode.Episodes = episodes;
                }
                else
                {
                    throw new NzbDroneClientException(HttpStatusCode.NotFound, "Unable to find matching series and episodes, will need to be manually provided");
                }
            }
            else if (remoteEpisode.Episodes.Empty())
            {
                var episodes = _parsingService.GetEpisodes(remoteEpisode.ParsedEpisodeInfo, remoteEpisode.Series, true);

                if (episodes.Empty() && release.SearchInfo?.EpisodeId.HasValue == true)
                {
                    var episode = _episodeService.GetEpisode(release.SearchInfo.EpisodeId.Value);

                    episodes = new List<Episode> { episode };
                }

                remoteEpisode.Episodes = episodes;
            }

            if (remoteEpisode.Episodes.Empty())
            {
                throw new NzbDroneClientException(HttpStatusCode.NotFound, "Unable to parse episodes in the release, will need to be manually provided");
            }

            await _downloadService.DownloadReport(remoteEpisode, release.Override?.DownloadClientId);
        }
        catch (ReleaseDownloadException ex)
        {
            _logger.Error(ex, ex.Message);
            throw new NzbDroneClientException(HttpStatusCode.Conflict, $"Getting release from indexer failed: {ex.Message}");
        }

        return TypedResults.Ok(release);
    }

    [HttpGet]
    [Produces("application/json")]
    public async Task<Results<Ok<List<ReleaseResource>>, BadRequest>> GetReleases(int? seriesId, int? episodeId, int? seasonNumber)
    {
        if (episodeId.HasValue)
        {
            return TypedResults.Ok(await GetEpisodeReleases(episodeId.Value));
        }

        if (seriesId.HasValue && seasonNumber.HasValue)
        {
            return TypedResults.Ok(await GetSeasonReleases(seriesId.Value, seasonNumber.Value));
        }

        if (seriesId.HasValue || seasonNumber.HasValue)
        {
            throw new NzbDroneClientException(HttpStatusCode.BadRequest, "seriesId and seasonNumber must be provided together");
        }

        return TypedResults.Ok(await GetRss());
    }

    /// <summary>
    /// Progressive variant of GET /release. Streams application/x-ndjson frames:
    /// {"type":"releases","batch":n,"releases":[ReleaseResource]} (upserts keyed by release.guid),
    /// {"type":"indexerError","indexerId":n,"indexer":"name","message":"..."},
    /// {"type":"complete","order":[guid...],"total":n,"indexerErrors":n} (final ordering) and
    /// {"type":"error","message":"..."} (fatal, after the response has started).
    /// </summary>
    [HttpGet("stream")]
    [Produces("application/x-ndjson")]
    public async Task StreamReleases(int? seriesId, int? episodeId, int? seasonNumber, CancellationToken cancellationToken)
    {
        if (!episodeId.HasValue && !(seriesId.HasValue && seasonNumber.HasValue))
        {
            throw new NzbDroneClientException(HttpStatusCode.BadRequest, "episodeId or seriesId and seasonNumber must be provided");
        }

        var history = episodeId.HasValue
            ? _historyService.FindByEpisodeId(episodeId.Value)
            : _historyService.GetBySeason(seriesId!.Value, seasonNumber!.Value, null);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Accel-Buffering"] = "no";

        var sink = new ReleaseStreamSink(this, history, Response.Body, cancellationToken);

        try
        {
            var decisions = episodeId.HasValue
                ? await _releaseSearchService.EpisodeSearch(episodeId.Value, true, true, sink, cancellationToken)
                : await _releaseSearchService.SeasonSearch(seriesId!.Value, seasonNumber!.Value, false, false, true, true, sink, cancellationToken);

            var prioritized = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);

            await sink.CompleteAsync(prioritized);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("Interactive search stream cancelled by the client");
        }
        catch (Exception ex)
        {
            if (ex is not SearchFailedException)
            {
                _logger.Error(ex, "Streaming search failed: " + ex.Message);
            }

            await sink.FailAsync(ex.Message);
        }
    }

    private class ReleaseStreamSink : IReleaseSearchSink
    {
        private readonly ReleaseController _controller;
        private readonly List<EpisodeHistory> _history;
        private readonly Stream _output;
        private readonly CancellationToken _cancellationToken;
        private readonly JsonSerializerOptions _options;
        private readonly HashSet<string> _sent = new ();
        private readonly SemaphoreSlim _writeLock = new (1, 1);
        private int _batch;
        private int _indexerErrors;

        public ReleaseStreamSink(ReleaseController controller, List<EpisodeHistory> history, Stream output, CancellationToken cancellationToken)
        {
            _controller = controller;
            _history = history;
            _output = output;
            _cancellationToken = cancellationToken;
            _options = STJson.GetSerializerSettings();
        }

        public Task OnDecisions(IReadOnlyList<DownloadDecision> decisions)
        {
            return WriteDecisionsAsync(decisions);
        }

        public Task OnIndexerError(int indexerId, string indexerName, string message)
        {
            Interlocked.Increment(ref _indexerErrors);

            return WriteAsync(new { type = "indexerError", indexerId, indexer = indexerName, message });
        }

        public async Task CompleteAsync(List<DownloadDecision> prioritized)
        {
            // The final winners were normally streamed already; send any that were not, then the final ordering
            var missing = prioritized.Where(d => !_sent.Contains(d.RemoteEpisode.Release.Guid ?? string.Empty)).ToList();

            if (missing.Count > 0)
            {
                await WriteDecisionsAsync(missing);
            }

            foreach (var decision in prioritized)
            {
                // Refresh the cache so the final winner of each guid is the one that can be grabbed
                var release = decision.RemoteEpisode.Release;

                _controller._remoteEpisodeCache.Set(string.Concat(release.IndexerId, "_", release.Guid), decision.RemoteEpisode, TimeSpan.FromMinutes(30));
            }

            await WriteAsync(new
            {
                type = "complete",
                order = prioritized.Select(d => d.RemoteEpisode.Release.Guid ?? string.Empty).ToList(),
                total = prioritized.Count,
                indexerErrors = _indexerErrors
            });
        }

        public Task FailAsync(string message)
        {
            return WriteAsync(new { type = "error", message });
        }

        private Task WriteDecisionsAsync(IReadOnlyList<DownloadDecision> decisions)
        {
            var resources = new List<ReleaseResource>(decisions.Count);

            foreach (var decision in decisions)
            {
                var resource = decision.MapDecision(0, _controller._qualityProfile);

                resource.History = _controller.AddHistory(decision.RemoteEpisode.Release, _history);
                _controller._remoteEpisodeCache.Set(_controller.GetCacheKey(resource), decision.RemoteEpisode, TimeSpan.FromMinutes(30));
                _sent.Add(decision.RemoteEpisode.Release.Guid ?? string.Empty);

                resources.Add(resource);
            }

            return WriteAsync(new { type = "releases", batch = Interlocked.Increment(ref _batch), releases = resources });
        }

        private async Task WriteAsync(object frame)
        {
            await _writeLock.WaitAsync(_cancellationToken);

            try
            {
                await STJson.SerializeAsync(frame, _output, _options);
                await _output.WriteAsync(NewLine, _cancellationToken);
                await _output.FlushAsync(_cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private static readonly byte[] NewLine = [(byte)'\n'];
    }

    private async Task<List<ReleaseResource>> GetEpisodeReleases(int episodeId)
    {
        try
        {
            var decisions = await _releaseSearchService.EpisodeSearch(episodeId, true, true);
            var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);
            var history = _historyService.FindByEpisodeId(episodeId);

            return MapDecisions(prioritizedDecisions, history);
        }
        catch (SearchFailedException ex)
        {
            throw new NzbDroneClientException(HttpStatusCode.BadRequest, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Episode search failed: " + ex.Message);
            throw new NzbDroneClientException(HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    private async Task<List<ReleaseResource>> GetSeasonReleases(int seriesId, int seasonNumber)
    {
        try
        {
            var decisions = await _releaseSearchService.SeasonSearch(seriesId, seasonNumber, false, false, true, true);
            var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);
            var history = _historyService.GetBySeason(seriesId, seasonNumber, null);

            return MapDecisions(prioritizedDecisions, history);
        }
        catch (SearchFailedException ex)
        {
            throw new NzbDroneClientException(HttpStatusCode.BadRequest, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Season search failed: " + ex.Message);
            throw new NzbDroneClientException(HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    private async Task<List<ReleaseResource>> GetRss()
    {
        var reports = await _rssFetcherAndParser.Fetch();
        var decisions = _downloadDecisionMaker.GetRssDecision(reports);
        var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);

        return MapDecisions(prioritizedDecisions, new List<EpisodeHistory>());
    }

    private string GetCacheKey(ReleaseResource resource)
    {
        return string.Concat(resource.Release!.IndexerId, "_", resource.Release!.Guid);
    }

    private string GetCacheKey(ReleaseGrabResource resource)
    {
        return string.Concat(resource.IndexerId, "_", resource.Guid);
    }

    private List<ReleaseResource> MapDecisions(IEnumerable<DownloadDecision> decisions, List<EpisodeHistory> history)
    {
        var result = new List<ReleaseResource>();

        foreach (var downloadDecision in decisions)
        {
            var release = downloadDecision.MapDecision(result.Count, _qualityProfile);

            release.History = AddHistory(downloadDecision.RemoteEpisode.Release, history);
            _remoteEpisodeCache.Set(GetCacheKey(release), downloadDecision.RemoteEpisode, TimeSpan.FromMinutes(30));

            result.Add(release);
        }

        return result;
    }

    private ReleaseHistoryResource? AddHistory(ReleaseInfo release, List<EpisodeHistory> history)
    {
        var grabbed = history.FirstOrDefault(h => h.EventType == EpisodeHistoryEventType.Grabbed &&
                                                  ((h.Data.TryGetValue("guid", out var guid) && guid == release.Guid) ||
                                                   (h.Data.TryGetValue("nzbInfoUrl", out var infoUrl) && infoUrl.IsNotNullOrWhiteSpace() && infoUrl.Equals(release.InfoUrl, StringComparison.Ordinal))));

        if (grabbed == null && release.DownloadProtocol == DownloadProtocol.Torrent)
        {
            if (release is not TorrentInfo torrentInfo)
            {
                return null;
            }

            if (torrentInfo.InfoHash.IsNotNullOrWhiteSpace())
            {
                grabbed = history.FirstOrDefault(h => h.EventType == EpisodeHistoryEventType.Grabbed &&
                                                      ReleaseComparer.SameTorrent(new ReleaseComparerModel(h),
                                                          torrentInfo));
            }

            if (grabbed == null)
            {
                grabbed = history.FirstOrDefault(h => h.EventType == EpisodeHistoryEventType.Grabbed &&
                                                      h.SourceTitle == release.Title &&
                                                      (DownloadProtocol)Convert.ToInt32(
                                                          h.Data.GetValueOrDefault("protocol")) ==
                                                      DownloadProtocol.Torrent &&
                                                      ReleaseComparer.SameTorrent(new ReleaseComparerModel(h),
                                                          torrentInfo));
            }
        }
        else if (grabbed == null)
        {
            grabbed = history.FirstOrDefault(h => h.EventType == EpisodeHistoryEventType.Grabbed &&
                                                  ReleaseComparer.SameNzb(new ReleaseComparerModel(h),
                                                      release));
        }

        if (grabbed != null)
        {
            var resource = new ReleaseHistoryResource
            {
                Grabbed = grabbed.Date,
            };

            var failedHistory = history.FirstOrDefault(h => h.EventType == EpisodeHistoryEventType.DownloadFailed &&
                                                            h.DownloadId == grabbed.DownloadId);

            if (failedHistory != null)
            {
                resource.Failed = failedHistory.Date;
            }

            return resource;
        }

        return null;
    }
}
