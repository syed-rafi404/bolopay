namespace BoloPay.Web.Models;

/// <summary>Why the pipeline decided to distrust what it heard.</summary>
public enum ConfidenceFlag
{
    /// <summary>avg_logprob fell below the floor — the model was unsure.</summary>
    LowConfidence,

    /// <summary>no_speech_prob was high — possibly noise rather than speech.</summary>
    PossibleNonSpeech,

    /// <summary>compression_ratio was outside normal speech range.</summary>
    UnusualPattern,

    /// <summary>Two transcription passes disagreed on the amount.</summary>
    AmountDisagreement,

    /// <summary>Two transcription passes disagreed on the recipient.</summary>
    RecipientDisagreement,

    /// <summary>
    /// The spoken name only cleared the fuzzy-match threshold by a small
    /// margin. The match may well be right, but "probably Adiba" is not good
    /// enough to present as settled fact on a payment screen.
    /// </summary>
    WeakContactMatch,
}

/// <summary>
/// The measured signals behind a flag decision. Surfaced to the client in
/// development so thresholds can be calibrated against real recordings
/// rather than guessed at.
/// </summary>
public sealed record ConfidenceMetrics(
    double WorstAvgLogprob,
    double WorstNoSpeechProb,
    double WorstCompressionRatio,
    int SegmentCount);

public sealed record ConfidenceResult(
    bool NeedsConfirmation,
    IReadOnlyList<ConfidenceFlag> Flags,
    string? Reason,
    ConfidenceMetrics Metrics)
{
    /// <summary>
    /// True when the uncertainty points specifically at the amount, so the UI
    /// can make that one field editable instead of the whole transaction.
    /// </summary>
    public bool AmountUncertain =>
        Flags.Contains(ConfidenceFlag.AmountDisagreement) || IsBroadlyUncertain;

    public bool RecipientUncertain =>
        Flags.Contains(ConfidenceFlag.RecipientDisagreement)
        || Flags.Contains(ConfidenceFlag.WeakContactMatch)
        || IsBroadlyUncertain;

    /// <summary>
    /// Segment-level metrics can't localise which word was misheard, so any
    /// segment-level flag has to taint every field. Over-flagging is the
    /// defensible failure mode when money is involved.
    /// </summary>
    private bool IsBroadlyUncertain =>
        Flags.Contains(ConfidenceFlag.LowConfidence)
        || Flags.Contains(ConfidenceFlag.PossibleNonSpeech)
        || Flags.Contains(ConfidenceFlag.UnusualPattern);
}
