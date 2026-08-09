using System.Text.Json.Serialization;

namespace BoloPay.Web.Models;

[JsonConverter(typeof(JsonStringEnumConverter<CommandIntent>))]
public enum CommandIntent
{
    [JsonStringEnumMemberName("send_money")]
    SendMoney,

    [JsonStringEnumMemberName("check_balance")]
    CheckBalance,

    [JsonStringEnumMemberName("unrecognized")]
    Unrecognized,
}

/// <summary>
/// What the extraction LLM returns. Shape must stay in sync with the JSON
/// schema in <see cref="Services.GroqIntentExtractor"/>, which runs in
/// strict mode — every property is required and nullable ones are unions.
/// </summary>
public sealed record VoiceCommand
{
    [JsonPropertyName("intent")]
    public CommandIntent Intent { get; init; } = CommandIntent.Unrecognized;

    [JsonPropertyName("amount_bdt")]
    public decimal? AmountBdt { get; init; }

    [JsonPropertyName("recipient_name")]
    public string? RecipientName { get; init; }

    /// <summary>
    /// The number as literally spoken, kept verbatim so the UI can show what
    /// was heard next to the digits it was parsed into.
    /// </summary>
    [JsonPropertyName("raw_number_phrase")]
    public string? RawNumberPhrase { get; init; }
}
