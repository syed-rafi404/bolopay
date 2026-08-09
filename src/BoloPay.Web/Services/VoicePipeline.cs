using BoloPay.Web.Models;
using Microsoft.Extensions.Options;

namespace BoloPay.Web.Services;

/// <summary>
/// Runs the full voice-command pipeline:
///   audio -> transcription (x2) -> intent extraction -> confidence -> contact match
/// </summary>
public sealed class VoicePipeline(
    ITranscriptionService transcription,
    IIntentExtractor extractor,
    ConfidenceScorer scorer,
    ContactMatcher matcher,
    IOptions<GroqOptions> groqOptions,
    IOptions<ConfidenceOptions> confidenceOptions,
    ILogger<VoicePipeline> logger)
{
    private readonly GroqOptions _groq = groqOptions.Value;
    private readonly ConfidenceOptions _confidence = confidenceOptions.Value;

    public async Task<ProcessResult> ProcessAsync(
        Stream audio,
        string fileName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        // The stream is read twice (once per ASR pass), so buffer it. Uploads
        // are capped at a couple of megabytes, so this is safe in memory.
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, cancellationToken);

        var primaryPass = await TranscribeAsync(
            buffer, fileName, contentType, _groq.TranscriptionModel, cancellationToken);

        if (string.IsNullOrWhiteSpace(primaryPass.Text))
        {
            logger.LogInformation("No speech detected in upload {FileName}", fileName);
            return new ProcessResult { Status = ResultStatus.NoSpeech };
        }

        // Second pass through a different model. Costs one extra ASR call and
        // buys a signal that segment-level metrics cannot provide: whether two
        // independent transcriptions agree on the amount and the recipient.
        TranscriptionPass? crossPass = null;
        if (_groq.EnableCrossCheck)
        {
            try
            {
                crossPass = await TranscribeAsync(
                    buffer, fileName, contentType, _groq.CrossCheckModel, cancellationToken);
            }
            catch (Exception ex)
            {
                // Losing the cross-check degrades the safety net to signal 1
                // only; it must never take down the whole request.
                logger.LogWarning(ex, "Cross-check transcription failed; continuing with primary only.");
            }
        }

        var primaryCommand = await extractor.ExtractAsync(primaryPass.Text, cancellationToken);

        VoiceCommand? crossCommand = null;
        if (crossPass is not null)
        {
            // Only spend a second extraction call when the transcripts actually
            // differ — identical text cannot disagree once parsed.
            crossCommand = string.Equals(
                crossPass.Text, primaryPass.Text, StringComparison.OrdinalIgnoreCase)
                ? primaryCommand
                : await extractor.ExtractAsync(crossPass.Text, cancellationToken);
        }

        // Resolve the contact before scoring so a weak fuzzy match can feed the
        // confidence decision — "probably Adiba" should look uncertain, not settled.
        ContactMatch? contactMatch = null;
        if (primaryCommand.Intent == CommandIntent.SendMoney && primaryCommand.AmountBdt is not null)
            contactMatch = matcher.Match(primaryCommand.RecipientName);

        var confidence = scorer.Score(
            primaryPass.Segments, primaryCommand, crossCommand, contactMatch);

        logger.LogInformation(
            "Pipeline result: intent={Intent} amount={Amount} recipient={Recipient} flags=[{Flags}]",
            primaryCommand.Intent,
            primaryCommand.AmountBdt,
            primaryCommand.RecipientName,
            string.Join(", ", confidence.Flags));

        return Build(primaryPass, primaryCommand, confidence, crossPass, crossCommand, contactMatch);
    }

    private async Task<TranscriptionPass> TranscribeAsync(
        MemoryStream buffer,
        string fileName,
        string contentType,
        string model,
        CancellationToken cancellationToken)
    {
        buffer.Position = 0;
        return await transcription.TranscribeAsync(
            buffer, fileName, contentType, model, cancellationToken);
    }

    private ProcessResult Build(
        TranscriptionPass pass,
        VoiceCommand command,
        ConfidenceResult confidence,
        TranscriptionPass? crossPass,
        VoiceCommand? crossCommand,
        ContactMatch? contactMatch)
    {
        var diagnostics = _confidence.ExposeDiagnostics
            ? new
            {
                primaryModel = pass.Model,
                primaryText = pass.Text,
                crossModel = crossPass?.Model,
                crossText = crossPass?.Text,
                crossAmount = crossCommand?.AmountBdt,
                crossRecipient = crossCommand?.RecipientName,
                contactMatchScore = contactMatch?.Score,
                contactMatchExact = contactMatch?.IsExact,
                worstAvgLogprob = confidence.Metrics.WorstAvgLogprob,
                worstNoSpeechProb = confidence.Metrics.WorstNoSpeechProb,
                worstCompressionRatio = confidence.Metrics.WorstCompressionRatio,
                segmentCount = confidence.Metrics.SegmentCount,
                thresholds = new
                {
                    avgLogprobFloor = _confidence.AvgLogprobFloor,
                    noSpeechProbCeiling = _confidence.NoSpeechProbCeiling,
                    compressionRatioMin = _confidence.CompressionRatioMin,
                    compressionRatioMax = _confidence.CompressionRatioMax,
                },
            }
            : null;

        var baseResult = new ProcessResult
        {
            Status = ResultStatus.Unrecognized,
            Transcript = pass.Text,
            Flags = confidence.Flags.Select(f => f.ToString()).ToArray(),
            Diagnostics = diagnostics,
        };

        if (command.Intent == CommandIntent.CheckBalance)
            return baseResult with { Status = ResultStatus.Balance };

        if (command.Intent != CommandIntent.SendMoney || command.AmountBdt is null)
            return baseResult;

        var match = contactMatch
                    ?? throw new InvalidOperationException("Send-money command reached Build without a contact match.");

        if (!match.Found)
        {
            return baseResult with
            {
                Status = ResultStatus.UnknownRecipient,
                AmountBdt = command.AmountBdt,
                RawNumberPhrase = command.RawNumberPhrase,
                RecipientHeard = command.RecipientName,
            };
        }

        // Mock ledger sanity check. The client enforces this too, but the rule
        // belongs on the server — a demo about transaction safety should not
        // let its own ledger be told "yes, transfer it" over a limit.
        if (command.AmountBdt > MockData.StartingBalance)
        {
            return baseResult with
            {
                Status = ResultStatus.OverBalance,
                AmountBdt = command.AmountBdt,
                RawNumberPhrase = command.RawNumberPhrase,
                RecipientHeard = command.RecipientName,
                RecipientName = match.Contact!.Name,
                RecipientBanglaName = match.Contact.BanglaName,
                RecipientPhone = match.Contact.Phone,
                ConfidenceReason = confidence.Reason,
            };
        }

        return baseResult with
        {
            Status = ResultStatus.Confirm,
            AmountBdt = command.AmountBdt,
            RawNumberPhrase = command.RawNumberPhrase,
            RecipientHeard = command.RecipientName,
            RecipientName = match.Contact!.Name,
            RecipientBanglaName = match.Contact.BanglaName,
            RecipientPhone = match.Contact.Phone,
            NeedsConfirmation = confidence.NeedsConfirmation,
            AmountUncertain = confidence.AmountUncertain,
            RecipientUncertain = confidence.RecipientUncertain,
            ConfidenceReason = confidence.Reason,
        };
    }
}
