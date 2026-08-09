using BoloPay.Web.Models;
using FuzzySharp;
using Microsoft.Extensions.Options;

namespace BoloPay.Web.Services;

/// <summary>
/// Resolves a spoken name against the mock contact list: exact match first,
/// then fuzzy. Nothing cleverer, because contact resolution isn't the point of
/// the demo — the confidence layer is.
/// </summary>
public sealed class ContactMatcher(IOptions<ConfidenceOptions> options)
{
    private readonly ConfidenceOptions _options = options.Value;

    public ContactMatch Match(string? spokenName)
    {
        if (string.IsNullOrWhiteSpace(spokenName))
            return new ContactMatch(null, 0, false);

        var needle = Normalise(spokenName);

        foreach (var contact in MockData.Contacts)
        {
            if (Normalise(contact.Name) == needle || Normalise(contact.BanglaName) == needle)
                return new ContactMatch(contact, 100, true);
        }

        MockContact? best = null;
        var bestScore = 0;

        foreach (var contact in MockData.Contacts)
        {
            // Score against both scripts — the ASR may return either, and a
            // Banglish transcript can romanise the name.
            var score = Math.Max(
                Fuzz.PartialRatio(needle, Normalise(contact.Name)),
                Fuzz.PartialRatio(needle, Normalise(contact.BanglaName)));

            if (score > bestScore)
            {
                bestScore = score;
                best = contact;
            }
        }

        return bestScore >= _options.ContactMatchThreshold
            ? new ContactMatch(best, bestScore, false)
            : new ContactMatch(null, bestScore, false);
    }

    /// <summary>
    /// Strips the Bangla case suffixes that turn আদিবা into আদিবাকে or আদিবার,
    /// which would otherwise drag down an exact match.
    /// </summary>
    private static string Normalise(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();

        foreach (var suffix in (string[])["কে", "র", "য়ের", "এর", "়"])
        {
            if (trimmed.Length > suffix.Length + 1 && trimmed.EndsWith(suffix, StringComparison.Ordinal))
            {
                trimmed = trimmed[..^suffix.Length];
                break;
            }
        }

        return trimmed;
    }
}
