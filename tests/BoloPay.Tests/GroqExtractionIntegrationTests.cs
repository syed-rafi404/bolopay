using System.Net.Http.Headers;
using BoloPay.Web.Models;
using BoloPay.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoloPay.Tests;

/// <summary>
/// Hits the real Groq API using the production <see cref="GroqIntentExtractor"/>.
///
/// These are skipped automatically when no key is configured, so the default
/// suite stays offline and deterministic. Run them with a key to verify the
/// assumptions the spec flagged as unverified: that strict json_schema mode
/// works on Groq, and that gpt-oss-120b converts Bangla number words correctly.
///
/// The key is read from the web project's user-secrets, so nothing lands in
/// source control.
///
/// Note: the free tier allows 8,000 tokens/minute on gpt-oss-120b, and the
/// system prompt alone is ~1,900 tokens, so these tests pace themselves.
/// </summary>
public class GroqExtractionIntegrationTests
{
    private const string WebProjectUserSecretsId = "fe173823-574a-43ce-955e-ff7fed1ecaa3";

    /// <summary>Free-tier TPM headroom is thin; keep requests spaced out.</summary>
    private static readonly TimeSpan Pace = TimeSpan.FromSeconds(20);

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

    private static GroqIntentExtractor? CreateExtractor()
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

        return new GroqIntentExtractor(
            http,
            Options.Create(options),
            NullLogger<GroqIntentExtractor>.Instance);
    }

    [SkippableTheory]
    [InlineData("আদিবার নাম্বারে পাঁচশো টাকা পাঠাও", 500)]
    [InlineData("তানভিরকে নয়শো টাকা পাঠাও", 900)]
    [InlineData("আম্মাকে এক হাজার টাকা পাঠাও", 1000)]
    [InlineData("আম্মাকে পঞ্চাশ হাজার টাকা পাঠাও", 50000)]
    public async Task ConvertsBanglaNumberWordsToDigits(string transcript, decimal expected)
    {
        var extractor = CreateExtractor();
        Skip.If(extractor is null, "No Groq key configured.");

        var result = await extractor!.ExtractAsync(transcript);

        Assert.Equal(CommandIntent.SendMoney, result.Intent);
        Assert.Equal(expected, result.AmountBdt);
        Assert.False(string.IsNullOrWhiteSpace(result.RecipientName));

        await Task.Delay(Pace);
    }

    [SkippableFact]
    public async Task RecognisesBalanceQuery()
    {
        var extractor = CreateExtractor();
        Skip.If(extractor is null, "No Groq key configured.");

        var result = await extractor!.ExtractAsync("আমার ব্যালেন্স কত");

        Assert.Equal(CommandIntent.CheckBalance, result.Intent);
        Assert.Null(result.AmountBdt);

        await Task.Delay(Pace);
    }

    [SkippableFact]
    public async Task RejectsOffTopicSpeech()
    {
        var extractor = CreateExtractor();
        Skip.If(extractor is null, "No Groq key configured.");

        var result = await extractor!.ExtractAsync("আজকে আবহাওয়া খুব সুন্দর");

        Assert.Equal(CommandIntent.Unrecognized, result.Intent);
        Assert.Null(result.AmountBdt);

        await Task.Delay(Pace);
    }

    [SkippableFact]
    public async Task NeverInventsAnUnstatedAmount()
    {
        // The safety-critical case: a send command with no amount must return
        // null rather than a plausible-looking guess.
        var extractor = CreateExtractor();
        Skip.If(extractor is null, "No Groq key configured.");

        var result = await extractor!.ExtractAsync("আদিবাকে টাকা পাঠাও");

        Assert.Null(result.AmountBdt);

        await Task.Delay(Pace);
    }
}
