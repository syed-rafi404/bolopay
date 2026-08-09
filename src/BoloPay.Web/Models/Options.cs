namespace BoloPay.Web.Models;

/// <summary>
/// Bound from the "Groq" configuration section. Model IDs are configurable
/// because Groq's catalogue changes with weeks' notice, and because the
/// ASR model needs to be swappable during threshold calibration.
/// </summary>
public sealed class GroqOptions
{
    public const string SectionName = "Groq";

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1";

    /// <summary>
    /// Primary ASR model. Defaults to whisper-large-v3 (10.3% WER) rather than
    /// -turbo (12%): both sit on the same free-tier limits, and for 2-4 second
    /// commands accuracy matters more than turbo's speed advantage.
    /// </summary>
    public string TranscriptionModel { get; set; } = "whisper-large-v3";

    /// <summary>
    /// Second ASR pass used for cross-pass agreement. A different model is
    /// deliberate: two independent errors are less likely to coincide than
    /// the same model erring twice.
    /// </summary>
    public string CrossCheckModel { get; set; } = "whisper-large-v3-turbo";

    /// <summary>Only gpt-oss-20b and gpt-oss-120b support strict structured output on Groq.</summary>
    public string ExtractionModel { get; set; } = "openai/gpt-oss-120b";

    public string Language { get; set; } = "bn";

    /// <summary>
    /// Whether to run the second transcription pass. Costs one extra ASR call
    /// per request (and one extra LLM call only when the transcripts differ).
    /// </summary>
    public bool EnableCrossCheck { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Section 7 thresholds. These are starting points derived from Groq's own
/// documented "healthy" example (avg_logprob ~= -0.10, no_speech_prob ~= 0.01,
/// compression_ratio ~= 1.66) — NOT measurements. They live in configuration
/// precisely so Phase 3 can move them without a rebuild.
/// </summary>
public sealed class ConfidenceOptions
{
    public const string SectionName = "Confidence";

    public double AvgLogprobFloor { get; set; } = -0.5;

    public double NoSpeechProbCeiling { get; set; } = 0.4;

    public double CompressionRatioMax { get; set; } = 2.2;

    public double CompressionRatioMin { get; set; } = 1.0;

    /// <summary>
    /// Fuzzy-match score below which a spoken name is treated as not matching
    /// any known contact.
    /// </summary>
    public int ContactMatchThreshold { get; set; } = 70;

    /// <summary>
    /// Fuzzy-match score below which a matched name is treated as uncertain.
    /// Scores between this and <see cref="ContactMatchThreshold"/> are "probably
    /// right, but not settled" — enough to resolve, not enough to present
    /// confidently on a payment screen.
    /// </summary>
    public int ContactMatchConfidentThreshold { get; set; } = 85;

    /// <summary>Expose measured metrics in the API response for calibration.</summary>
    public bool ExposeDiagnostics { get; set; }
}
