using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoloPay.Web.Models;
using Microsoft.Extensions.Options;

namespace BoloPay.Web.Services;

public interface IIntentExtractor
{
    Task<VoiceCommand> ExtractAsync(string transcript, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a Bangla/Banglish transcript into a structured command.
///
/// A second LLM call rather than regex because Bangla number words, casual
/// phrasing and code-switching make rule-based parsing brittle. Runs in strict
/// structured-output mode, which on Groq is only supported by the gpt-oss
/// models and uses constrained decoding — the response cannot violate the
/// schema, so no retry or repair logic is needed.
/// </summary>
public sealed class GroqIntentExtractor(
    HttpClient http,
    IOptions<GroqOptions> options,
    ILogger<GroqIntentExtractor> logger) : IIntentExtractor
{
    private readonly GroqOptions _options = options.Value;

    private const string SystemPrompt = """
        You are a transaction-command parser for a Bangladeshi mobile payment app.
        You receive a Bangla or Banglish transcript of a spoken command.

        Classify intent as one of:
          - "send_money"     the speaker wants to transfer money to someone
          - "check_balance"  the speaker is asking about their balance
          - "unrecognized"   anything else, including unclear or off-topic speech

        For send_money, extract:
          - amount_bdt: the amount in BDT as a plain number. Convert Bangla
            number words to digits (একশো=100, দুইশো=200, তিনশো=300, চারশো=400,
            পাঁচশো=500, ছয়শো=600, সাতশো=700, আটশো=800, নয়শো=900,
            এক হাজার=1000, দুই হাজার=2000, পঞ্চাশ হাজার=50000).
          - recipient_name: the recipient's name exactly as spoken, stripped of
            Bangla case suffixes (আদিবাকে/আদিবার -> আদিবা).
          - raw_number_phrase: the exact substring of the transcript containing
            the number, verbatim.

        Rules:
          - Never invent an amount that was not stated. Return null instead.
          - Never invent a recipient. Return null instead.
          - For check_balance and unrecognized, all three extracted fields are null.
        """;

    public async Task<VoiceCommand> ExtractAsync(
        string transcript,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return new VoiceCommand { Intent = CommandIntent.Unrecognized };

        var request = new JsonObject
        {
            ["model"] = _options.ExtractionModel,
            ["temperature"] = 0,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "voice_command",
                    // Constrained decoding. Requires every property in `required`
                    // and additionalProperties:false on every object.
                    ["strict"] = true,
                    ["schema"] = BuildSchema(),
                },
            },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = transcript },
            },
        };

        using var response = await http.PostAsJsonAsync(
            "chat/completions", request, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Groq extraction failed ({Status}): {Body}", (int)response.StatusCode, body);

            throw new GroqException(
                $"Intent extraction failed with status {(int)response.StatusCode}.",
                response.StatusCode);
        }

        var content = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(content))
            throw new GroqException("Intent extraction returned an empty response.");

        logger.LogInformation("Extraction -> {Content}", content);

        var command = JsonSerializer.Deserialize<VoiceCommand>(content)
                      ?? throw new GroqException("Intent extraction returned unreadable JSON.");

        return Sanitise(command);
    }

    /// <summary>
    /// Trust nothing that came from a model. Strict mode guarantees the shape,
    /// not the sense: it can still return a negative amount or a stray suffix.
    /// </summary>
    private static VoiceCommand Sanitise(VoiceCommand command)
    {
        var amount = command.AmountBdt;

        if (amount is <= 0 or > 10_000_000)
            amount = null;

        // Whole taka only — no realistic spoken command means 500.37.
        if (amount is not null)
            amount = Math.Round(amount.Value, 0, MidpointRounding.AwayFromZero);

        return command with
        {
            AmountBdt = amount,
            RecipientName = string.IsNullOrWhiteSpace(command.RecipientName)
                ? null
                : command.RecipientName.Trim(),
            RawNumberPhrase = string.IsNullOrWhiteSpace(command.RawNumberPhrase)
                ? null
                : command.RawNumberPhrase.Trim(),
        };
    }

    private static JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["intent"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "send_money", "check_balance", "unrecognized" },
            },
            ["amount_bdt"] = Nullable("number"),
            ["recipient_name"] = Nullable("string"),
            ["raw_number_phrase"] = Nullable("string"),
        },
        ["required"] = new JsonArray
        {
            "intent", "amount_bdt", "recipient_name", "raw_number_phrase",
        },
        ["additionalProperties"] = false,
    };

    /// <summary>Strict mode has no optional fields; nullability is a union type.</summary>
    private static JsonObject Nullable(string type) => new()
    {
        ["type"] = new JsonArray { type, "null" },
    };
}
