using System.Net.Http.Headers;
using BoloPay.Web.Models;
using BoloPay.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace BoloPay.Tests;

/// <summary>
/// Answers the calibration question the PowerShell probes could not: does the
/// EXTRACTED AMOUNT stay stable across sampled passes on clear audio, while
/// diverging on degraded audio?
///
/// Raw transcripts always differ under sampling, so comparing transcript text
/// is uselessly noisy. The scorer compares parsed fields, and this measures
/// exactly that. Written in C# because PowerShell 5.1 cannot parse Bangla
/// string literals, which corrupted every earlier attempt.
///
/// Skipped without a key. Deliberately not part of the normal suite: it makes
/// many ASR calls and is a measurement tool, not an assertion.
/// </summary>
public class AmountStabilityCalibration(ITestOutputHelper output)
{
    private const string WebProjectUserSecretsId = "fe173823-574a-43ce-955e-ff7fed1ecaa3";
    private const int Repetitions = 3;

    private static readonly string SampleDir =
        Path.Combine("G:", "CV", "BL", "src", "BoloPay.Web", "wwwroot", "sample-audio");

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

    private static (GroqTranscriptionService Asr, GroqIntentExtractor Extractor)? Create(string key)
    {
        var options = new GroqOptions { ApiKey = key };

        HttpClient Build()
        {
            var http = new HttpClient
            {
                BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            };
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
            return http;
        }

        return (
            new GroqTranscriptionService(Build(), Options.Create(options),
                NullLogger<GroqTranscriptionService>.Instance),
            new GroqIntentExtractor(Build(), Options.Create(options),
                NullLogger<GroqIntentExtractor>.Instance));
    }

    [SkippableTheory]
    [InlineData(0.4f)]
    [InlineData(0.8f)]
    public async Task MeasureAmountStability(float crossTemperature)
    {
        var key = ResolveApiKey();
        Skip.If(key is null, "No Groq key configured.");
        Skip.If(!Directory.Exists(SampleDir), "Sample audio not found.");

        var (asr, extractor) = Create(key!)!.Value;

        (string File, bool Clean)[] clips =
        [
            ("01-clean-adiba-500.wav", true),
            ("02-clean-tanvir-900.wav", true),
            ("03a-mumble-mild.wav", false),
            ("03b-mumble-heavy.wav", false),
        ];

        output.WriteLine($"=== cross-check temperature {crossTemperature} ===");

        foreach (var (file, clean) in clips)
        {
            var path = Path.Combine(SampleDir, file);
            if (!File.Exists(path)) continue;

            var bytes = await File.ReadAllBytesAsync(path);

            var greedy = await ExtractAmount(asr, extractor, bytes, file, 0f);

            var samples = new List<decimal?>();
            for (var i = 0; i < Repetitions; i++)
                samples.Add(await ExtractAmount(asr, extractor, bytes, file, crossTemperature));

            // Only a confident-vs-confident mismatch counts, matching the
            // scorer: a null means that pass had no opinion, not a conflict.
            var disagreements = samples.Count(s =>
                s is not null && greedy is not null && s != greedy);

            var rendered = string.Join(", ", samples.Select(s => s?.ToString() ?? "null"));
            var verdict = clean
                ? (disagreements == 0 ? "GOOD (stable)" : "BAD (false positive)")
                : (disagreements > 0 ? "GOOD (caught)" : "MISS (not caught)");

            output.WriteLine(
                $"  {file,-26} greedy={greedy?.ToString() ?? "null",-6} " +
                $"samples=[{rendered}]  disagreed {disagreements}/{Repetitions}  {verdict}");
        }
    }

    private static async Task<decimal?> ExtractAmount(
        GroqTranscriptionService asr,
        GroqIntentExtractor extractor,
        byte[] audio,
        string fileName,
        float temperature)
    {
        using var stream = new MemoryStream(audio);

        var pass = await asr.TranscribeAsync(
            stream, fileName, "audio/wav", "whisper-large-v3", temperature);

        if (string.IsNullOrWhiteSpace(pass.Text)) return null;

        var command = await extractor.ExtractAsync(pass.Text);

        // Free tier is ~8000 TPM on the extraction model; pace to stay under it.
        await Task.Delay(TimeSpan.FromSeconds(6));

        return command.AmountBdt;
    }
}
