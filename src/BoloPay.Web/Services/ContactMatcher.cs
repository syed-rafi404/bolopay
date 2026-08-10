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
    /// Normalises a spoken name for comparison.
    ///
    /// Two things happen here, both driven by what the ASR actually returned
    /// during calibration:
    ///
    /// 1. Bangla case suffixes are stripped — আদিবা becomes আদিবাকে or আদিবার
    ///    depending on grammatical role, while the contact list stores the stem.
    ///
    /// 2. Hasanta (U+09CD, the conjunct-forming virama) and nukta (U+09BC) are
    ///    removed. Whisper returned "তান্ভির" for the contact "তানভির" — the same
    ///    name, differing only by a hasanta joining ন and ভ. That single
    ///    codepoint dropped the fuzzy score below the confidence threshold and
    ///    raised a spurious WeakContactMatch on a cleanly spoken clip.
    /// </summary>
    private static string Normalise(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();

        // Drop conjunct and nukta marks first, so suffix matching below runs
        // against a stable form.
        trimmed = new string(trimmed.Where(c => c is not ('\u09CD' or '\u09BC')).ToArray());

        // Longest first: "য়ের" must be tried before "র", otherwise the shorter
        // suffix strips one character and leaves a broken stem.
        foreach (var suffix in (string[])["য়ের", "এর", "কে", "র"])
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
