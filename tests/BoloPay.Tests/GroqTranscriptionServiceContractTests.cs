using System.Net;
using System.Net.Http.Headers;
using BoloPay.Web.Models;
using BoloPay.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoloPay.Tests;

/// <summary>
/// The cross-check safety net transcribes the same stream twice. It broke
/// silently because MultipartFormDataContent disposes the content it is given —
/// and therefore the caller's buffer — so the second pass died before ever
/// reaching the API. These tests pin the contract that prevents the regression:
/// a stream must survive being handed to the service more than once.
/// </summary>
public class GroqTranscriptionServiceContractTests
{
    /// <summary>
    /// Minimal fake Whisper handler. Returns one segment carrying the
    /// confidence fields so deserialisation actually runs, and records how many
    /// requests were made.
    /// </summary>
    private sealed class FakeGroqHandler : HttpMessageHandler
    {
        private const string Body = """
            {
              "text": "আমার ব্যালেন্স কত",
              "segments": [
                {
                  "id": 0,
                  "text": "আমার ব্যালেন্স কত",
                  "start": 0.0,
                  "end": 2.4,
                  "avg_logprob": -0.2,
                  "no_speech_prob": 0.01,
                  "compression_ratio": 1.3
                }
              ]
            }
            """;

        public int RequestCount { get; private set; }

        /// <summary>
        /// Length of the "file" part only. The full multipart body is not
        /// comparable across passes because the model name differs in length.
        /// </summary>
        public List<long> AudioByteCounts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            // Reading the parts forces the multipart body to be serialised,
            // which is what actually triggered the disposal bug in production.
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);

            foreach (var part in multipart)
            {
                if (part.Headers.ContentDisposition?.Name?.Trim('"') != "file")
                    continue;

                var audio = await part.ReadAsByteArrayAsync(cancellationToken);
                AudioByteCounts.Add(audio.LongLength);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body),
            };
        }
    }

    private static GroqTranscriptionService CreateService(HttpMessageHandler handler)
    {
        var options = new GroqOptions { ApiKey = "gsk_test" };

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
        };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ApiKey);

        return new GroqTranscriptionService(
            http,
            Options.Create(options),
            NullLogger<GroqTranscriptionService>.Instance);
    }

    [Fact]
    public async Task SameStream_CanBeTranscribedTwice()
    {
        // Regression test: the second pass must not throw ObjectDisposedException.
        using var handler = new FakeGroqHandler();
        var service = CreateService(handler);
        using var stream = new MemoryStream(new byte[64]);

        var first = await service.TranscribeAsync(
            stream, "command.wav", "audio/wav", "whisper-large-v3");
        var second = await service.TranscribeAsync(
            stream, "command.wav", "audio/wav", "whisper-large-v3-turbo");

        Assert.Equal("whisper-large-v3", first.Model);
        Assert.Equal("whisper-large-v3-turbo", second.Model);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task BothPasses_UploadTheSameAudio()
    {
        // The cross-check is only meaningful if both models see identical audio.
        // A rewind bug would show up here as a short or empty second upload.
        using var handler = new FakeGroqHandler();
        var service = CreateService(handler);
        using var stream = new MemoryStream(new byte[4096]);

        await service.TranscribeAsync(stream, "command.wav", "audio/wav", "whisper-large-v3");
        await service.TranscribeAsync(stream, "command.wav", "audio/wav", "whisper-large-v3-turbo");

        Assert.Equal(2, handler.AudioByteCounts.Count);
        Assert.Equal(4096, handler.AudioByteCounts[0]);
        Assert.Equal(handler.AudioByteCounts[0], handler.AudioByteCounts[1]);
    }

    [Fact]
    public async Task CallerStream_IsNotDisposed()
    {
        using var handler = new FakeGroqHandler();
        var service = CreateService(handler);
        using var stream = new MemoryStream(new byte[64]);

        await service.TranscribeAsync(stream, "command.wav", "audio/wav", "whisper-large-v3");

        // The service must not dispose a stream it does not own.
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task ReturnsSegmentConfidenceFields()
    {
        using var handler = new FakeGroqHandler();
        var service = CreateService(handler);
        using var stream = new MemoryStream(new byte[64]);

        var pass = await service.TranscribeAsync(
            stream, "command.wav", "audio/wav", "whisper-large-v3");

        var segment = Assert.Single(pass.Segments);
        Assert.Equal(-0.2, segment.AvgLogprob, precision: 4);
        Assert.Equal(0.01, segment.NoSpeechProb, precision: 4);
        Assert.Equal(1.3, segment.CompressionRatio, precision: 4);
    }
}
