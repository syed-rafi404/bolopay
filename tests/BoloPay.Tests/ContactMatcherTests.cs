using BoloPay.Web.Models;
using BoloPay.Web.Services;
using Microsoft.Extensions.Options;

namespace BoloPay.Tests;

public class ContactMatcherTests
{
    private static readonly ConfidenceOptions Defaults = new();
    private static readonly ContactMatcher Matcher = new(Options.Create(Defaults));

    [Theory]
    [InlineData("আদিবা", "Adiba")]
    [InlineData("তানভির", "Tanvir")]
    [InlineData("আম্মা", "Amma")]
    public void ExactBanglaName_Matches(string spoken, string expected)
    {
        var match = Matcher.Match(spoken);

        Assert.True(match.Found);
        Assert.True(match.IsExact);
        Assert.Equal(expected, match.Contact!.Name);
    }

    [Theory]
    [InlineData("Adiba")]
    [InlineData("adiba")]
    [InlineData("ADIBA")]
    public void RomanisedName_MatchesCaseInsensitively(string spoken)
    {
        var match = Matcher.Match(spoken);

        Assert.True(match.Found);
        Assert.Equal("Adiba", match.Contact!.Name);
    }

    [Theory]
    [InlineData("আদিবাকে")]  // dative -কে
    [InlineData("আদিবার")]   // genitive -র
    public void BanglaCaseSuffix_IsStrippedBeforeMatching(string spoken)
    {
        // ASR returns the name as spoken, which in Bangla carries a case
        // suffix. Without stripping, "আদিবাকে" would never match "আদিবা".
        var match = Matcher.Match(spoken);

        Assert.True(match.Found);
        Assert.Equal("Adiba", match.Contact!.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInput_DoesNotMatch(string? spoken)
    {
        var match = Matcher.Match(spoken);

        Assert.False(match.Found);
        Assert.Null(match.Contact);
    }

    [Fact]
    public void UnknownName_DoesNotMatch()
    {
        // "রাকিব" is not in the contact list; this must reach the
        // unknown-recipient state rather than being forced onto a contact.
        var match = Matcher.Match("রাকিব");

        Assert.False(match.Found);
    }

    [Fact]
    public void CloseButNotExactName_MatchesFuzzily()
    {
        var match = Matcher.Match("Adibaa");

        Assert.True(match.Found);
        Assert.False(match.IsExact);
        Assert.Equal("Adiba", match.Contact!.Name);
    }

    [Fact]
    public void ExactMatch_ScoresFullMarks()
    {
        var match = Matcher.Match("Adiba");

        Assert.Equal(100, match.Score);
        Assert.True(match.IsExact);
    }
}
