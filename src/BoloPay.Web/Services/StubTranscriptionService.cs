using BoloPay.Web.Models;
using Microsoft.Extensions.Options;

namespace BoloPay.Web.Services;

/// <summary>
/// Offline transcriber used when no Groq key is configured. It exists so the
/// whole pipeline, UI and all, can be built and demonstrated before the real
/// Bangla recordings arrive — and so the app degrades into something coherent
/// rather than an error screen if the key is ever missing in production.
///
/// It fabricates segment metadata that mimics the shape of real Whisper
/// output, including deliberately poor values for the "mumbled" scenario.
/// These numbers are invented. They must never be used to calibrate
/// thresholds; only real recordings can do that.
///
/// Registered scoped so both passes of one request see the same scenario —
/// otherwise the rotation would advance between passes and manufacture a
/// disagreement that did not happen.
/// </summary>
public sealed class StubTranscriptionService(
    IOptions<GroqOptions> options,
    ILogger<StubTranscriptionService> logger) : ITranscriptionService
{
    private static int _rotationCounter = -1;

    private readonly GroqOptions _options = options.Value;
    private Scenario? _resolved;

    private sealed record Scenario(
        string Text,
        double AvgLogprob,
        double NoSpeechProb,
        double CompressionRatio,
        string? CrossCheckText = null);

    private static readonly Scenario[] Rotation =
    [
        new("আদিবার নাম্বারে পাঁচশো টাকা পাঠাও", -0.18, 0.02, 1.42),
        new("তানভিরকে নয়শো টাকা পাঠাও", -0.24, 0.03, 1.31),
        // The flagged case: poor logprob, and the second pass hears 900 instead
        // of 500 — the disagreement is what makes the amount editable.
        new("আদিবার নাম্বারে পাঁচশো টাকা পাঠাও", -0.72, 0.11, 1.28,
            CrossCheckText: "আদিবার নাম্বারে নয়শো টাকা পাঠাও"),
        new("আমার ব্যালেন্স কত", -0.21, 0.02, 1.15),
        new("আজকে আবহাওয়া খুব সুন্দর", -0.19, 0.04, 1.38),
        new("আম্মাকে পঞ্চাশ হাজার টাকা পাঠাও", -0.22, 0.03, 1.34),
    ];

    public Task<TranscriptionPass> TranscribeAsync(
        Stream audio,
        string fileName,
        string contentType,
        string model,
        CancellationToken cancellationToken = default)
    {
        var scenario = _resolved ??= Resolve(fileName);

        var isCrossCheck = !string.Equals(
            model, _options.TranscriptionModel, StringComparison.OrdinalIgnoreCase);

        var text = isCrossCheck && scenario.CrossCheckText is not null
            ? scenario.CrossCheckText
            : scenario.Text;

        logger.LogInformation(
            "Stub ASR [{Model}{Role}] -> {Text}",
            model, isCrossCheck ? " / cross-check" : " / primary", text);

        var segment = new WhisperSegment
        {
            Id = 0,
            Text = text,
            Start = 0,
            End = 3.2,
            AvgLogprob = scenario.AvgLogprob,
            NoSpeechProb = scenario.NoSpeechProb,
            CompressionRatio = scenario.CompressionRatio,
        };

        return Task.FromResult(new TranscriptionPass(model, text, [segment]));
    }

    /// <summary>
    /// Sample clips are named by scenario, so honour the filename when it is
    /// recognisable and otherwise rotate, which gives a live mic recording a
    /// different outcome on each press.
    /// </summary>
    private static Scenario Resolve(string fileName)
    {
        var name = fileName.ToLowerInvariant();

        if (name.Contains("mumble") || name.Contains("noisy") || name.Contains("stutter"))
            return Rotation[2];
        if (name.Contains("over-balance") || name.StartsWith("07"))
            return Rotation[5];
        if (name.Contains("balance"))
            return Rotation[3];
        if (name.Contains("nonsense") || name.Contains("offtopic"))
            return Rotation[4];
        if (name.Contains("tanvir") || name.StartsWith("02"))
            return Rotation[1];
        if (name.StartsWith("01") || name.Contains("clean"))
            return Rotation[0];

        var next = Interlocked.Increment(ref _rotationCounter);
        return Rotation[next % Rotation.Length];
    }
}
