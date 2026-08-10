using BoloPay.Web.Models;
using BoloPay.Web.Services;
using Microsoft.Extensions.Options;

namespace BoloPay.Tests;

public class ConfidenceScorerTests
{
    private static readonly ConfidenceOptions Defaults = new();
    private static readonly ConfidenceScorer Scorer = new(Options.Create(Defaults));

    private static WhisperSegment Segment(
        double avgLogprob = -0.1,
        double noSpeechProb = 0.01,
        double compressionRatio = 1.6) => new()
    {
        Id = 0,
        Text = "test",
        Start = 0,
        End = 3,
        AvgLogprob = avgLogprob,
        NoSpeechProb = noSpeechProb,
        CompressionRatio = compressionRatio,
    };

    private static VoiceCommand Send(
        decimal? amount = 500m,
        string? recipient = "আদিবা") => new()
    {
        Intent = CommandIntent.SendMoney,
        AmountBdt = amount,
        RecipientName = recipient,
    };

    [Fact]
    public void CleanSegments_NoFlags()
    {
        var result = Scorer.Score([Segment()], Send(), null);

        Assert.False(result.NeedsConfirmation);
        Assert.Empty(result.Flags);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void LowLogprob_FlagsEverythingAsBroadlyUncertain()
    {
        var result = Scorer.Score([Segment(avgLogprob: -0.9)], Send(), null);

        Assert.True(result.NeedsConfirmation);
        Assert.Contains(ConfidenceFlag.LowConfidence, result.Flags);
        Assert.True(result.AmountUncertain);
        Assert.True(result.RecipientUncertain);
    }

    [Fact]
    public void HighNoSpeechProb_FlagsPossibleNonSpeech()
    {
        var result = Scorer.Score([Segment(noSpeechProb: 0.7)], Send(), null);

        Assert.Contains(ConfidenceFlag.PossibleNonSpeech, result.Flags);
    }

    [Theory]
    [InlineData(3.5)]
    [InlineData(0.5)]
    public void CompressionRatioOutsideRange_FlagsUnusualPattern(double ratio)
    {
        var result = Scorer.Score([Segment(compressionRatio: ratio)], Send(), null);

        Assert.Contains(ConfidenceFlag.UnusualPattern, result.Flags);
    }

    [Fact]
    public void AmountDisagreement_FlagsOnlyTheAmount()
    {
        var result = Scorer.Score(
            [Segment()],
            Send(amount: 500m),
            Send(amount: 900m));

        Assert.Contains(ConfidenceFlag.AmountDisagreement, result.Flags);
        Assert.DoesNotContain(ConfidenceFlag.RecipientDisagreement, result.Flags);
        Assert.True(result.AmountUncertain);
        Assert.False(result.RecipientUncertain);
        Assert.Equal("The amount wasn't clear — please check it.", result.Reason);
    }

    [Fact]
    public void RecipientDisagreement_FlagsOnlyTheRecipient()
    {
        var result = Scorer.Score(
            [Segment()],
            Send(recipient: "আদিবা"),
            Send(recipient: "তানভির"));

        Assert.Contains(ConfidenceFlag.RecipientDisagreement, result.Flags);
        Assert.True(result.RecipientUncertain);
        Assert.False(result.AmountUncertain);
    }

    [Fact]
    public void OnlyPrimaryHeardAnAmount_IsDisagreement()
    {
        // The most dangerous case, and the one this signal exists for: one pass
        // is confident enough to move money while the other could not hear a
        // number at all. Measured on a noisy clip — the greedy pass read পাঁচশো
        // (500) while the sampled pass read পাপতো and extracted nothing.
        var result = Scorer.Score(
            [Segment()],
            Send(amount: 500m),
            Send(amount: null));

        Assert.Contains(ConfidenceFlag.AmountDisagreement, result.Flags);
        Assert.True(result.NeedsConfirmation);
        Assert.True(result.AmountUncertain);
    }

    [Fact]
    public void OnlyCrossPassHeardAnAmount_IsDisagreement()
    {
        // Symmetric case: the sampled pass found an amount the greedy pass did
        // not. Equally untrustworthy, so it must flag too.
        var result = Scorer.Score(
            [Segment()],
            Send(amount: null),
            Send(amount: 500m));

        Assert.Contains(ConfidenceFlag.AmountDisagreement, result.Flags);
    }

    [Fact]
    public void NeitherPassHeardAnAmount_IsNotDisagreement()
    {
        // Both silent means there is nothing to confirm. The command fails
        // elsewhere as unrecognised; flagging here would put a warning on a
        // screen that has no amount to warn about.
        var result = Scorer.Score(
            [Segment()],
            Send(amount: null),
            Send(amount: null));

        Assert.DoesNotContain(ConfidenceFlag.AmountDisagreement, result.Flags);
    }

    [Fact]
    public void OnlyOnePassHeardARecipient_IsDisagreement()
    {
        var result = Scorer.Score(
            [Segment()],
            Send(recipient: "আদিবা"),
            Send(recipient: null));

        Assert.Contains(ConfidenceFlag.RecipientDisagreement, result.Flags);
    }

    [Fact]
    public void NeitherPassHeardARecipient_IsNotDisagreement()
    {
        var result = Scorer.Score(
            [Segment()],
            Send(recipient: null),
            Send(recipient: null));

        Assert.DoesNotContain(ConfidenceFlag.RecipientDisagreement, result.Flags);
    }

    [Fact]
    public void BothFieldsDisagree_MessageCoversBoth()
    {
        var result = Scorer.Score(
            [Segment()],
            Send(amount: 500m, recipient: "আদিবা"),
            Send(amount: 900m, recipient: "তানভির"));

        Assert.Equal("The amount and the name weren't clear — please check both.", result.Reason);
    }

    [Fact]
    public void WeakContactMatch_FlagsRecipientUncertain()
    {
        var weak = new ContactMatch(MockData.Contacts[0], Score: 78, IsExact: false);
        var result = Scorer.Score([Segment()], Send(), null, weak);

        Assert.Contains(ConfidenceFlag.WeakContactMatch, result.Flags);
        Assert.True(result.NeedsConfirmation);
        Assert.True(result.RecipientUncertain);
    }

    [Fact]
    public void ExactContactMatch_DoesNotFlag()
    {
        var exact = new ContactMatch(MockData.Contacts[0], Score: 100, IsExact: true);
        var result = Scorer.Score([Segment()], Send(), null, exact);

        Assert.DoesNotContain(ConfidenceFlag.WeakContactMatch, result.Flags);
        Assert.False(result.NeedsConfirmation);
    }

    [Fact]
    public void SegmentLevelFlag_UsesGenericNotFieldSpecificCopy()
    {
        // Segment-level uncertainty cannot be localised, so the message must
        // not claim the amount specifically was the problem.
        var result = Scorer.Score([Segment(avgLogprob: -0.9)], Send(), null);

        Assert.DoesNotContain("amount", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Wasn't fully sure what was said — please check the details.", result.Reason);
    }
}
