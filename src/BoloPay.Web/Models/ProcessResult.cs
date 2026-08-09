using System.Text.Json.Serialization;

namespace BoloPay.Web.Models;

/// <summary>Outcome of matching a spoken name against the mock contact list.</summary>
public sealed record ContactMatch(
    MockContact? Contact,
    int Score,
    bool IsExact)
{
    public bool Found => Contact is not null;
}

/// <summary>What the UI should render next.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResultStatus>))]
public enum ResultStatus
{
    /// <summary>Valid send_money command, recipient resolved.</summary>
    [JsonStringEnumMemberName("confirm")]
    Confirm,

    /// <summary>Balance enquiry — nothing to confirm.</summary>
    [JsonStringEnumMemberName("balance")]
    Balance,

    /// <summary>Speech understood, but no valid transaction command in it.</summary>
    [JsonStringEnumMemberName("unrecognized")]
    Unrecognized,

    /// <summary>Command parsed but the named person isn't in the contact list.</summary>
    [JsonStringEnumMemberName("unknown_recipient")]
    UnknownRecipient,

    /// <summary>Valid command, but the amount exceeds the mock balance.</summary>
    [JsonStringEnumMemberName("over_balance")]
    OverBalance,

    /// <summary>Nothing usable came back from transcription at all.</summary>
    [JsonStringEnumMemberName("no_speech")]
    NoSpeech,
}

/// <summary>The single response shape the browser consumes.</summary>
public sealed record ProcessResult
{
    [JsonPropertyName("status")]
    public required ResultStatus Status { get; init; }

    [JsonPropertyName("transcript")]
    public string Transcript { get; init; } = string.Empty;

    [JsonPropertyName("amountBdt")]
    public decimal? AmountBdt { get; init; }

    [JsonPropertyName("rawNumberPhrase")]
    public string? RawNumberPhrase { get; init; }

    [JsonPropertyName("recipientHeard")]
    public string? RecipientHeard { get; init; }

    [JsonPropertyName("recipientName")]
    public string? RecipientName { get; init; }

    [JsonPropertyName("recipientBanglaName")]
    public string? RecipientBanglaName { get; init; }

    [JsonPropertyName("recipientPhone")]
    public string? RecipientPhone { get; init; }

    [JsonPropertyName("needsConfirmation")]
    public bool NeedsConfirmation { get; init; }

    [JsonPropertyName("amountUncertain")]
    public bool AmountUncertain { get; init; }

    [JsonPropertyName("recipientUncertain")]
    public bool RecipientUncertain { get; init; }

    [JsonPropertyName("confidenceReason")]
    public string? ConfidenceReason { get; init; }

    [JsonPropertyName("flags")]
    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>
    /// Raw measured values. Populated only in Development — this is the data
    /// used to calibrate thresholds in Phase 3, not something a visitor sees.
    /// </summary>
    [JsonPropertyName("diagnostics")]
    public object? Diagnostics { get; init; }
}
