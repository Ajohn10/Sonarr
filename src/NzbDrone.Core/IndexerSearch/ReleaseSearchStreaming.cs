using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.IndexerSearch
{
    /// <summary>
    /// Receives incremental results of a release search. Calls are serialized by the search service.
    /// </summary>
    public interface IReleaseSearchSink
    {
        /// <summary>
        /// New or improved decisions, keyed by release guid. A decision for a guid that was already
        /// delivered replaces the earlier one (de-dupe winner changed).
        /// </summary>
        Task OnDecisions(IReadOnlyList<DownloadDecision> decisions);

        Task OnIndexerError(int indexerId, string indexerName, string message);
    }

    /// <summary>
    /// Per-indexer search context flowing with the async call chain so indexers can honour
    /// cancellation and publish partial pages without changing the IIndexer contract.
    /// </summary>
    public class IndexerSearchScope
    {
        private readonly Func<IList<ReleaseInfo>, Task> _onReleases;
        private readonly HashSet<object> _published = new HashSet<object>(ReferenceEqualityComparer.Instance);
        private readonly List<string> _errors = new List<string>();
        private readonly object _lock = new object();

        public IndexerSearchScope(CancellationToken token, Func<IList<ReleaseInfo>, Task> onReleases)
        {
            Token = token;
            _onReleases = onReleases;
        }

        public CancellationToken Token { get; }

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (_lock)
                {
                    return _errors.ToList();
                }
            }
        }

        public void ReportError(string message)
        {
            lock (_lock)
            {
                _errors.Add(message);
            }
        }

        /// <summary>
        /// Publishes releases that have not been published yet (by reference).
        /// </summary>
        public async Task PublishAsync(IEnumerable<ReleaseInfo> releases)
        {
            List<ReleaseInfo> fresh;

            lock (_lock)
            {
                fresh = releases.Where(r => _published.Add(r)).ToList();
            }

            if (fresh.Count > 0)
            {
                await _onReleases(fresh);
            }
        }
    }

    public static class ReleaseSearchScope
    {
        private static readonly AsyncLocal<IndexerSearchScope> Scope = new AsyncLocal<IndexerSearchScope>();

        public static IndexerSearchScope Current => Scope.Value;

        public static IDisposable Use(IndexerSearchScope scope)
        {
            var previous = Scope.Value;
            Scope.Value = scope;

            return new Restore(previous);
        }

        private class Restore : IDisposable
        {
            private readonly IndexerSearchScope _previous;

            public Restore(IndexerSearchScope previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                Scope.Value = _previous;
            }
        }
    }

    internal class ReleaseSearchStream : IDisposable
    {
        private readonly Dictionary<string, DownloadDecision> _best = new Dictionary<string, DownloadDecision>();
        private readonly CancellationTokenSource _cts;

        public ReleaseSearchStream(IReleaseSearchSink sink, CancellationToken cancellationToken)
        {
            Sink = sink;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public IReleaseSearchSink Sink { get; }

        // Serializes decision making and sink calls (decision specifications were never run concurrently before)
        public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);

        public CancellationToken Token => _cts.Token;

        public void Abort()
        {
            _cts.Cancel();
        }

        // Keyed upsert: O(1) per decision, keeps the same winner as the non-streaming guid de-dupe
        // (fewest rejections, then lowest indexer priority, then first arrival).
        public List<DownloadDecision> Upsert(IEnumerable<DownloadDecision> decisions)
        {
            var changedKeys = new List<string>();
            var seen = new HashSet<string>();

            foreach (var decision in decisions)
            {
                var key = decision.RemoteEpisode.Release.Guid ?? string.Empty;

                if (_best.TryGetValue(key, out var current) && !IsBetter(decision, current))
                {
                    continue;
                }

                _best[key] = decision;

                if (seen.Add(key))
                {
                    changedKeys.Add(key);
                }
            }

            return changedKeys.Select(k => _best[k]).ToList();
        }

        private static bool IsBetter(DownloadDecision candidate, DownloadDecision current)
        {
            var candidateRejections = candidate.Rejections.Count();
            var currentRejections = current.Rejections.Count();

            if (candidateRejections != currentRejections)
            {
                return candidateRejections < currentRejections;
            }

            return GetPriority(candidate) < GetPriority(current);
        }

        private static int GetPriority(DownloadDecision decision)
        {
            return decision.RemoteEpisode?.Release?.IndexerPriority ?? IndexerDefinition.DefaultPriority;
        }

        public void Dispose()
        {
            _cts.Dispose();
            Gate.Dispose();
        }
    }
}
