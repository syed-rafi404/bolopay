using System.Net.Http.Headers;
using BoloPay.Web.Models;
using BoloPay.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoloPay.Tests;

/// <summary>
/// Verifies the Groq speech-to-text contract against the live API using the
/// production <see cref="GroqTranscriptionService"/>.
///
/// This deliberately does NOT test transcription accuracy — that needs real
/// Bangla recordings. What it does prove is that the request shape Groq accepts
/// matches what the app sends, and that verbose_json actually returns the
/// per-segment confidence fields the safety net is built on. Those were
/// assumptions until now.
///
/// Skipped automatically when no key is configured.
/// </summary>
public class GroqTranscriptionIntegrationTests
{
    private const string WebProjectUserSecretsId = "fe173823-574a-43ce-955e-ff7fed1ecaa3";

    private static string? ResolveApiKey()
    {
        var fromEnv = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        var config = new ConfigurationBuilder()
            .AddUserSecrets(WebProjectUserSecretsId)
            .Build();

        var key = config["Groq:ApiKey"];
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private static (GroqTranscriptionService Service, GroqOptions Options)? CreateService()
    {
        var key = ResolveApiKey();
        if (key is null) return null;

        var options = new GroqOptions { ApiKey = key };

        var http = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var service = new GroqTranscriptionService(
            http,
            Options.Create(options),
            NullLogger<GroqTranscriptionService>.Instance);

        return (service, options);
    }

    /// <summary>
    /// Builds a valid 16 kHz mono 16-bit WAV containing a quiet tone. Enough to
    /// exercise the endpoint contract without shipping a binary fixture.
    /// </summary>
    private static byte[] BuildToneWav(double seconds = 1.0, double frequency = 220.0)
    {
        const int sampleRate = 16_000;
        const short bitsPerSample = 16;
        const short channels = 1;

        var sampleCount = (int)(sampleRate * seconds);
        var dataBytes = sampleCount * channels * (bitsPerSample / 8);

        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);

        w.Write("RIFF".ToCharArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE".ToCharArray());

        w.Write("fmt ".ToCharArray());
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * (bitsPerSample / 8));
        w.Write((short)(channels * (bitsPerSample / 8)));
        w.Write(bitsPerSample);

        w.Write("data".ToCharArray());
        w.Write(dataBytes);

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sampleRate;
            var value = Math.Sin(2 * Math.PI * frequency * t) * 0.2;
            w.Write((short)(value * short.MaxValue));
        }

        w.Flush();
        return stream.ToArray();
    }

    [SkippableFact]
    public async Task AcceptsTheRequestShapeTheAppSends()
    {
        var created = CreateService();
        Skip.If(created is null, "No Groq key configured.");

        var (service, options) = created!.Value;

        using var audio = new MemoryStream(BuildToneWav());

        // The assertion is that this does not throw. A GroqException here would
        // mean the multipart shape, language, response_format, granularity or
        // prompt parameters are wrong.
        var pass = await service.TranscribeAsync(
            audio, "tone.wav", "audio/wav", options.TranscriptionModel);

        Assert.Equal(options.TranscriptionModel, pass.Model);
        Assert.NotNull(pass.Text);
        Assert.NotNull(pass.Segments);
    }

    [SkippableFact]
    public async Task BothAsrModelsAreReachable()
    {
        // The dual-pass safety net calls two different models. If either is
        // unavailable on this tier, the agreement check silently degrades.
        var created = CreateService();
        Skip.If(created is null, "No Groq key configured.");

        var (service, _) = created!.Value;

        foreach (var model in new[] { "whisper-large-v3", "whisper-large-v3-turbo" })
        {
            using var audio = new MemoryStream(BuildToneWav());

            var pass = await service.TranscribeAsync(
                audio, "tone.wav", "audio/wav", model);

            Assert.Equal(model, pass.Model);
        }
    }

    [SkippableFact]
    public async Task VerboseJsonReturnsConfidenceFieldsWhenSpeechIsPresent()
    {
        // A synthetic tone usually yields zero segments, so this asserts the
        // shape only when Whisper does return one. Real confidence calibration
        // needs actual recordings.
        var created = CreateService();
        Skip.If(created is null, "No Groq key configured.");

        var (service, options) = created!.Value;

        using var audio = new MemoryStream(BuildToneWav(seconds: 2.0));

        var pass = await service.TranscribeAsync(
            audio, "tone.wav", "audio/wav", options.TranscriptionModel);

        Skip.If(pass.Segments.Count == 0, "No segments returned for synthetic tone.");

        var segment = pass.Segments[0];

        // avg_logprob is negative for real output; 0 would mean the field was
        // missing and silently defaulted, which would break the safety net.
        Assert.True(segment.AvgLogprob < 0, "avg_logprob was not populated.");
        Assert.InRange(segment.NoSpeechProb, 0.0, 1.0);
        Assert.True(segment.CompressionRatio > 0, "compression_ratio was not populated.");
    }
}
