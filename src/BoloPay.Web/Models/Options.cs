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
    /// Model for the second (cross-check) pass. Defaults to the SAME model as
    /// the primary pass, deliberately.
    ///
    /// The original design used whisper-large-v3-turbo here. Calibration against
    /// real Bangla recordings showed that turbo mangled the number word in every
    /// clip that contained one — including both clean recordings — so
    /// "disagreement" measured turbo's weaker Bangla rather than whether the
    /// audio was ambiguous. A signal that fires on every input discriminates
    /// nothing.
    ///
    /// Sampling one model twice removes that confound: the only variable left
    /// is the acoustics.
    /// </summary>
    public string CrossCheckModel { get; set; } = "whisper-large-v3";

    /// <summary>
    /// Temperature for the primary pass. Zero is greedy decoding, so this pass
    /// is reproducible — the value the user is shown never changes between runs.
    /// </summary>
    public float PrimaryTemperature { get; set; } = 0f;

    /// <summary>
    /// Temperature for the cross-check pass. Non-zero makes Whisper sample
    /// rather than take the argmax at each step. Where the audio is
    /// unambiguous, sampling lands on the same tokens and the two passes agree.
    /// Where it is ambiguous, the passes diverge — which is exactly the
    /// condition worth flagging on a payment screen.
    /// </summary>
    public float CrossCheckTemperature { get; set; } = 0.4f;

    /// <summary>Only gpt-oss-20b and gpt-oss-120b support strict structured output on Groq.</summary>
    public string ExtractionModel { get; set; } = "openai/gpt-oss-120b";

    public string Language { get; set; } = "bn";

    /// <summary>
    /// Whether to seed the Whisper prompt with the mock contact names.
    ///
    /// Measured both ways on the sample clips (3 runs each). Seeding the names
    /// is clearly better and is therefore the default:
    ///
    ///   with names    12/12 known-contact runs correct
    ///   without names  6/15 correct, and amounts corrupted — "পঞ্চাশ হাজার"
    ///                  (50,000) was read as "পঞ্চাশ" (50), a 1000x error on a
    ///                  payment screen
    ///
    /// Removing the names also did not fix the unfamiliar-name case it was
    /// meant to fix: the placeholder names used in the prompt examples simply
    /// became the new bias, and "রাকিব" was transcribed as "রহিম".
    ///
    /// The underlying trade-off is inherent to Whisper prompting: biasing
    /// toward a known vocabulary improves that vocabulary and degrades
    /// everything outside it. For a transfer flow whose recipients are always
    /// drawn from a contact list, biasing toward the contact list is the right
    /// call — an unrecognised name should fail closed, not be guessed at.
    /// </summary>
    public bool IncludeContactNamesInPrompt { get; set; } = true;

    /// <summary>
    /// Whether to run the second transcription pass. Costs one extra ASR call
    /// per request (and one extra LLM call only when the transcripts differ).
    /// </summary>
    public bool EnableCrossCheck { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Section 7 thresholds.
///
/// AvgLogprobFloor, NoSpeechProbCeiling and the compression band were seeded
/// from Groq's documented "healthy" example. They have since been measured
/// against ten real Bangla recordings — see recordings/calibration-results.csv
/// and the README. The headline result: avg_logprob does NOT separate clear
/// speech from mumbled speech in Bangla. Every clip scored between -0.012 and
/// -0.126, and the most heavily mumbled clip scored *better* than both clean
/// ones. The floor is kept as a guard against catastrophic input, not as a
/// working discriminator.
/// </summary>
public sealed class ConfidenceOptions
{
    public const string SectionName = "Confidence";

    /// <summary>
    /// Measured range on real recordings was -0.012 to -0.126, so this never
    /// fires in practice. Retained as a floor for genuinely broken audio.
    /// </summary>
    public double AvgLogprobFloor { get; set; } = -0.5;

    public double NoSpeechProbCeiling { get; set; } = 0.4;

    public double CompressionRatioMax { get; set; } = 2.2;

    /// <summary>
    /// Lowered from 1.0 after calibration. Short Bangla utterances compress
    /// poorly: "আমার ব্যালেন্স কত?" measured 0.9245 and tripped a spurious
    /// UnusualPattern flag purely for being short.
    /// </summary>
    public double CompressionRatioMin { get; set; } = 0.85;

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
