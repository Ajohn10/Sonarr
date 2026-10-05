using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers
{
    /// <summary>
    /// Write-only stream that parses an application/x-ndjson indexer response line by line while it is being received.
    /// Prowlarr contract v1 record types: "start", "batch" ({ "sequence", "releases": [ ... ] }), terminal "complete" or
    /// "error" ({ "code", "message", "retryAfterSeconds" }). Unknown types are ignored; no terminal record means truncated.
    /// </summary>
    public class NdjsonReleaseStream : Stream
    {
        public const string MediaType = "application/x-ndjson";

        private readonly DownloadProtocol _protocol;
        private readonly Func<IList<ReleaseInfo>, Task> _onReleases;
        private readonly List<byte> _pending = new List<byte>();
        private readonly List<ReleaseInfo> _releases = new List<ReleaseInfo>();
        private readonly List<string> _indexerErrors = new List<string>();

        public NdjsonReleaseStream(DownloadProtocol protocol, Func<IList<ReleaseInfo>, Task> onReleases)
        {
            _protocol = protocol;
            _onReleases = onReleases;
        }

        public IList<ReleaseInfo> Releases => _releases;
        public IReadOnlyList<string> IndexerErrors => _indexerErrors;
        public string Error { get; private set; }
        public int? ErrorCode { get; private set; }
        public int? RetryAfterSeconds { get; private set; }
        public bool Completed { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var start = 0;

            while (start < buffer.Length)
            {
                var newline = AppendUntilNewline(buffer.Span.Slice(start));

                if (newline < 0)
                {
                    break;
                }

                start += newline + 1;

                await ProcessPendingLineAsync();
            }
        }

        private int AppendUntilNewline(ReadOnlySpan<byte> bytes)
        {
            var newline = bytes.IndexOf((byte)'\n');

            AppendPending(newline < 0 ? bytes : bytes.Slice(0, newline));

            return newline;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count)).AsTask().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Processes a trailing record that wasn't terminated by a newline.
        /// </summary>
        public Task CompleteAsync()
        {
            return ProcessPendingLineAsync();
        }

        private void AppendPending(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                _pending.Add(b);
            }
        }

        private async Task ProcessPendingLineAsync()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var line = _pending.ToArray();
            _pending.Clear();

            await ProcessLineAsync(line);
        }

        private async Task ProcessLineAsync(byte[] line)
        {
            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // Skip a malformed record instead of discarding everything received so far
                _indexerErrors.Add("Received a malformed record from the indexer");
                return;
            }

            using (document)
            {
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement))
                {
                    return;
                }

                switch (typeElement.GetString())
                {
                    case "batch":
                        if (root.TryGetProperty("releases", out var items) && items.ValueKind == JsonValueKind.Array)
                        {
                            var batch = new List<ReleaseInfo>();

                            foreach (var item in items.EnumerateArray())
                            {
                                var release = ParseRelease(item);

                                if (release != null)
                                {
                                    batch.Add(release);
                                }
                            }

                            _releases.AddRange(batch);

                            if (batch.Count > 0)
                            {
                                await _onReleases(batch);
                            }
                        }

                        break;

                    case "error":
                        Error = GetString(root, "message") ?? "Indexer reported an error";
                        ErrorCode = (int?)GetLong(root, "code");
                        RetryAfterSeconds = (int?)GetLong(root, "retryAfterSeconds");
                        break;

                    case "complete":
                        Completed = true;
                        break;
                }
            }
        }

        private ReleaseInfo ParseRelease(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var release = _protocol == DownloadProtocol.Torrent ? new TorrentInfo() : new ReleaseInfo();

            release.Guid = GetString(item, "guid");
            release.Title = GetString(item, "title");
            release.Size = GetLong(item, "size") ?? 0;
            release.DownloadUrl = GetString(item, "downloadUrl");
            release.InfoUrl = GetString(item, "infoUrl");
            release.TvdbId = (int)(GetLong(item, "tvdbId") ?? 0);
            release.TvRageId = (int)(GetLong(item, "tvRageId") ?? 0);
            var imdbId = GetLong(item, "imdbId");

            if (imdbId > 0)
            {
                release.ImdbId = $"tt{imdbId:D7}";
            }

            var publishDate = GetString(item, "publishDate");

            if (publishDate != null && DateTime.TryParse(publishDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
            {
                release.PublishDate = date;
            }

            if (item.TryGetProperty("languages", out var languages) && languages.ValueKind == JsonValueKind.Array)
            {
                foreach (var language in languages.EnumerateArray())
                {
                    var mapped = FindLanguage(language.ValueKind == JsonValueKind.String ? language.GetString() : GetString(language, "name"));

                    if (mapped != null)
                    {
                        release.Languages.Add(mapped);
                    }
                }
            }

            if (item.TryGetProperty("flags", out var flags) && flags.ValueKind == JsonValueKind.Array)
            {
                foreach (var flag in flags.EnumerateArray())
                {
                    if (flag.ValueKind == JsonValueKind.String && Enum.TryParse<IndexerFlags>(flag.GetString(), true, out var parsed))
                    {
                        release.IndexerFlags |= parsed;
                    }
                }
            }

            if (release is TorrentInfo torrent)
            {
                torrent.MagnetUrl = release.DownloadUrl?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true ? release.DownloadUrl : null;
                torrent.InfoHash = GetString(item, "infoHash");
                torrent.Seeders = (int?)GetLong(item, "seeders");
                torrent.Peers = (int?)GetLong(item, "peers");
            }

            return release;
        }

        private static Language FindLanguage(string name)
        {
            return Language.All.Find(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetString(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static long? GetLong(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
