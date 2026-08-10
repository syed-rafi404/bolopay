using BoloPay.Web.Models;
using Microsoft.Extensions.Options;

namespace BoloPay.Web.Services;

/// <summary>
/// Decides when to distrust the transcription. Two independent signals:
///
/// 1. Segment-level quality metrics from Whisper (avg_logprob, no_speech_prob,
///    compression_ratio). Cheap, but avg_logprob is averaged across every token
///    in the segment, so a single misheard digit in a 4-second command barely
///    moves it. Bangla also sits well below Whisper's average WER, which means
///    a threshold tuned on English intuition risks separating "this is Bangla"
///    from "this is English" rather than "clear" from "mumbled".
///
/// 2. Cross-pass agreement on the extracted fields. Two different Whisper
///    models transcribe the same audio; if the resulting amount or recipient
///    disagree, that targets exactly the two values where being wrong costs
///    money — and it works regardless of Bangla's absolute logprob range.
///
/// Either signal is enough to flag. Signal 2 exists because signal 1 may well
/// turn out flat once measured against real recordings.
/// </summary>
public sealed class ConfidenceScorer(IOptions<ConfidenceOptions> options)
{
    private readonly ConfidenceOptions _options = options.Value;

    public ConfidenceResult Score(
        IReadOnlyList<WhisperSegment> segments,
        VoiceCommand primary,
        VoiceCommand? crossCheck,
        ContactMatch? contactMatch = null)
    {
        var flags = new List<ConfidenceFlag>();

        // --- Signal 1: segment quality -------------------------------------
        var worstLogprob = 0.0;
        var worstNoSpeech = 0.0;
        var worstCompression = 0.0;

        if (segments.Count > 0)
        {
            worstLogprob = segments.Min(s => s.AvgLogprob);
            worstNoSpeech = segments.Max(s => s.NoSpeechProb);

            // "Worst" compression means furthest from the healthy midpoint,
            // since both unusually high and unusually low values are suspect.
            var midpoint = (_options.CompressionRatioMin + _options.CompressionRatioMax) / 2;
            worstCompression = segments
                .OrderByDescending(s => Math.Abs(s.CompressionRatio - midpoint))
                .First()
                .CompressionRatio;

            if (worstLogprob < _options.AvgLogprobFloor)
                flags.Add(ConfidenceFlag.LowConfidence);

            if (worstNoSpeech > _options.NoSpeechProbCeiling)
                flags.Add(ConfidenceFlag.PossibleNonSpeech);

            if (worstCompression > _options.CompressionRatioMax
                || worstCompression < _options.CompressionRatioMin)
                flags.Add(ConfidenceFlag.UnusualPattern);
        }

        // --- Signal 2: cross-pass agreement --------------------------------
        //
        // Both passes use the same model at different temperatures, so a
        // difference here reflects ambiguous audio rather than one model being
        // weaker at Bangla. (The original design compared two different models;
        // calibration showed turbo mangled the number word on every clip
        // containing one, so the flag fired on clean speech too.)
        //
        // A null on one side means that pass failed to extract a value, not
        // that it extracted a conflicting one. Treating that as disagreement
        // made the clean demo flag intermittently, since the extractor is not
        // deterministic on a garbled token. Only flag when both passes are
        // confident AND they differ.
        if (crossCheck is not null)
        {
            if (primary.AmountBdt is not null
                && crossCheck.AmountBdt is not null
                && primary.AmountBdt != crossCheck.AmountBdt)
            {
                flags.Add(ConfidenceFlag.AmountDisagreement);
            }

            if (!string.IsNullOrWhiteSpace(primary.RecipientName)
                && !string.IsNullOrWhiteSpace(crossCheck.RecipientName)
                && !NamesAgree(primary.RecipientName, crossCheck.RecipientName))
            {
                flags.Add(ConfidenceFlag.RecipientDisagreement);
            }
        }

        // --- Signal 3: how convincing the contact match actually was ---------
        // A fuzzy score that only just cleared the threshold is a guess wearing
        // a confident face. Exact matches are exempt.
        if (contactMatch is { Found: true, IsExact: false }
            && contactMatch.Score < _options.ContactMatchConfidentThreshold)
        {
            flags.Add(ConfidenceFlag.WeakContactMatch);
        }

        var metrics = new ConfidenceMetrics(
            worstLogprob, worstNoSpeech, worstCompression, segments.Count);

        return new ConfidenceResult(
            NeedsConfirmation: flags.Count > 0,
            Flags: flags,
            Reason: DescribeReason(flags),
            Metrics: metrics);
    }

    private static bool NamesAgree(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return true;
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)
               || FuzzySharp.Fuzz.Ratio(a.Trim().ToLowerInvariant(), b.Trim().ToLowerInvariant()) >= 85;
    }

    /// <summary>
    /// User-facing copy. Deliberately plain: the point is to prompt a check,
    /// not to explain log probabilities to someone sending money.
    ///
    /// Field-specific wording is only used when the uncertainty is actually
    /// localisable — that is, when cross-pass disagreement fired on its own.
    /// A segment-level flag taints the whole utterance and makes every field
    /// editable, so claiming "the amount wasn't clear" in that case would tell
    /// the user something the pipeline does not know.
    /// </summary>
    private static string? DescribeReason(List<ConfidenceFlag> flags)
    {
        if (flags.Count == 0) return null;

        var segmentLevel = flags.Where(IsSegmentLevel).ToList();

        if (segmentLevel.Count == 0)
        {
            var amount = flags.Contains(ConfidenceFlag.AmountDisagreement);
            var recipient = flags.Contains(ConfidenceFlag.RecipientDisagreement);

            return (amount, recipient) switch
            {
                (true, true) => "The amount and the name weren't clear — please check both.",
                (true, false) => "The amount wasn't clear — please check it.",
                (false, true) => "The name wasn't clear — please check it.",
                _ => "Wasn't fully sure about this — please check it.",
            };
        }

        // Segment-level uncertainty: report the cause, but keep the scope broad.
        if (segmentLevel.Contains(ConfidenceFlag.PossibleNonSpeech))
            return "Background noise detected — please check the details.";

        if (segmentLevel.Contains(ConfidenceFlag.UnusualPattern))
            return "The speech was hard to follow — please check the details.";

        return "Wasn't fully sure what was said — please check the details.";
    }

    private static bool IsSegmentLevel(ConfidenceFlag flag) => flag
        is ConfidenceFlag.LowConfidence
        or ConfidenceFlag.PossibleNonSpeech
        or ConfidenceFlag.UnusualPattern;
}
