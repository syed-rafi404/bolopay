using BoloPay.Web.Models;

namespace BoloPay.Web.Services;

/// <summary>
/// Offline intent extraction for when no Groq key is configured. Deliberately
/// crude keyword-and-lookup matching over the stub transcripts only.
///
/// This is not a fallback for the real thing. Section 5.2 of the spec is right
/// that rule-based parsing is too brittle for genuine Bangla input — that is
/// the whole reason the production path uses an LLM. This exists so the UI can
/// be built and clicked through without a key.
/// </summary>
public sealed class StubIntentExtractor(ILogger<StubIntentExtractor> logger) : IIntentExtractor
{
    private static readonly (string Word, decimal Value)[] NumberWords =
    [
        ("পঞ্চাশ হাজার", 50000m),
        ("দুই হাজার", 2000m),
        ("এক হাজার", 1000m),
        ("হাজার", 1000m),
        ("একশো", 100m),
        ("দুইশো", 200m),
        ("তিনশো", 300m),
        ("চারশো", 400m),
        ("পাঁচশো", 500m),
        ("ছয়শো", 600m),
        ("সাতশো", 700m),
        ("আটশো", 800m),
        ("নয়শো", 900m),
    ];

    private static readonly string[] SendVerbs = ["পাঠাও", "পাঠাও।", "দাও", "send", "pathao"];
    private static readonly string[] BalanceWords = ["ব্যালেন্স", "balance", "কত টাকা আছে"];

    public Task<VoiceCommand> ExtractAsync(
        string transcript,
        CancellationToken cancellationToken = default)
    {
        var result = Parse(transcript);
        logger.LogInformation(
            "Stub extraction -> intent={Intent} amount={Amount} recipient={Recipient}",
            result.Intent, result.AmountBdt, result.RecipientName);

        return Task.FromResult(result);
    }

    private static VoiceCommand Parse(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return new VoiceCommand { Intent = CommandIntent.Unrecognized };

        var text = transcript.Trim();

        if (BalanceWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return new VoiceCommand { Intent = CommandIntent.CheckBalance };

        var hasSendVerb = SendVerbs.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));
        var amountMatch = NumberWords.FirstOrDefault(n => text.Contains(n.Word));

        if (!hasSendVerb || amountMatch.Word is null)
            return new VoiceCommand { Intent = CommandIntent.Unrecognized };

        return new VoiceCommand
        {
            Intent = CommandIntent.SendMoney,
            AmountBdt = amountMatch.Value,
            RecipientName = FindRecipient(text),
            RawNumberPhrase = amountMatch.Word,
        };
    }

    /// <summary>
    /// Matches the known Bangla contact names, tolerating the case suffixes
    /// Bangla attaches to them (আদিবাকে, আদিবার).
    /// </summary>
    private static string? FindRecipient(string text)
    {
        foreach (var contact in MockData.Contacts)
        {
            if (text.Contains(contact.BanglaName, StringComparison.Ordinal))
                return contact.BanglaName;

            if (text.Contains(contact.Name, StringComparison.OrdinalIgnoreCase))
                return contact.Name;
        }

        // Unknown-recipient path: pull the token before the send verb so the
        // "not in your contacts" state can be exercised offline too.
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verbIndex = Array.FindIndex(
            tokens, t => SendVerbs.Any(v => t.Contains(v, StringComparison.OrdinalIgnoreCase)));

        return verbIndex > 0 ? tokens[0].TrimEnd(',') : null;
    }
}
