using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Test.IndexerTests
{
    [TestFixture]
    public class NdjsonReleaseStreamFixture
    {
        private List<IList<ReleaseInfo>> _batches;

        [SetUp]
        public void SetUp()
        {
            _batches = new List<IList<ReleaseInfo>>();
        }

        private NdjsonReleaseStream Create(DownloadProtocol protocol)
        {
            return new NdjsonReleaseStream(protocol, batch =>
            {
                _batches.Add(batch);

                return Task.CompletedTask;
            });
        }

        private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        [Test]
        public async Task should_publish_each_batch_and_parse_torrent_fields()
        {
            var stream = Create(DownloadProtocol.Torrent);

            await stream.WriteAsync(Bytes("{\"type\":\"start\",\"version\":1,\"indexerId\":3,\"protocol\":\"torrent\"}\n"));
            await stream.WriteAsync(Bytes("{\"type\":\"batch\",\"sequence\":1,\"releases\":[{\"guid\":\"g1\",\"title\":\"Show S01E01\",\"size\":1234,\"downloadUrl\":\"magnet:?xt=1\",\"imdbId\":944947,\"seeders\":10,\"peers\":15,\"infoHash\":\"abc\",\"flags\":[\"freeleech\"],\"languages\":[\"English\"],\"publishDate\":\"2024-01-02T03:04:05Z\"}]}\n"));
            await stream.WriteAsync(Bytes("{\"type\":\"batch\",\"sequence\":2,\"releases\":[{\"guid\":\"g2\",\"title\":\"Show S01E02\"}]}\n"));
            await stream.WriteAsync(Bytes("{\"type\":\"complete\",\"batches\":2,\"releaseCount\":2}\n"));
            await stream.CompleteAsync();

            _batches.Should().HaveCount(2);
            stream.Completed.Should().BeTrue();
            stream.Error.Should().BeNull();
            stream.Releases.Select(r => r.Guid).Should().Equal("g1", "g2");

            var torrent = stream.Releases[0].Should().BeOfType<TorrentInfo>().Subject;
            torrent.Size.Should().Be(1234);
            torrent.ImdbId.Should().Be("tt0944947");
            torrent.Seeders.Should().Be(10);
            torrent.Peers.Should().Be(15);
            torrent.InfoHash.Should().Be("abc");
            torrent.MagnetUrl.Should().Be("magnet:?xt=1");
            torrent.IndexerFlags.Should().HaveFlag(IndexerFlags.Freeleech);
            torrent.Languages.Select(l => l.Name).Should().Equal("English");
        }

        [Test]
        public async Task should_handle_lines_split_across_chunks_and_missing_trailing_newline()
        {
            var stream = Create(DownloadProtocol.Usenet);
            var data = Bytes("{\"type\":\"batch\",\"sequence\":1,\"releases\":[{\"guid\":\"g1\",\"title\":\"t\"}]}\n{\"type\":\"complete\"}");

            for (var i = 0; i < data.Length; i += 7)
            {
                await stream.WriteAsync(data, i, System.Math.Min(7, data.Length - i));
            }

            await stream.CompleteAsync();

            stream.Releases.Should().ContainSingle().Which.Should().NotBeOfType<TorrentInfo>();
            stream.Completed.Should().BeTrue();
        }

        [Test]
        public async Task should_keep_partial_results_and_capture_terminal_error()
        {
            var stream = Create(DownloadProtocol.Usenet);

            await stream.WriteAsync(Bytes("{\"type\":\"batch\",\"sequence\":1,\"releases\":[{\"guid\":\"g1\"}]}\n"));
            await stream.WriteAsync(Bytes("{\"type\":\"error\",\"code\":429,\"message\":\"disabled\",\"retryAfterSeconds\":60,\"batches\":1,\"releaseCount\":1}\n"));
            await stream.CompleteAsync();

            stream.Releases.Should().HaveCount(1);
            stream.Error.Should().Be("disabled");
            stream.ErrorCode.Should().Be(429);
            stream.RetryAfterSeconds.Should().Be(60);
            stream.Completed.Should().BeFalse();
        }

        [Test]
        public async Task should_skip_malformed_and_unknown_records_and_report_truncation()
        {
            var stream = Create(DownloadProtocol.Usenet);

            await stream.WriteAsync(Bytes("not json\n{\"type\":\"future\"}\n{\"type\":\"batch\",\"releases\":[{\"guid\":\"g1\"}]}\n"));
            await stream.CompleteAsync();

            stream.Releases.Should().HaveCount(1);
            stream.Completed.Should().BeFalse();
        }
    }
}
