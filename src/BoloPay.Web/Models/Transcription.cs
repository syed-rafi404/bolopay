using System.Text.Json.Serialization;

namespace BoloPay.Web.Models;

/// <summary>
/// One segment of Whisper's verbose_json output. The three quality fields
/// (avg_logprob, no_speech_prob, compression_ratio) only exist at segment
/// level — Whisper does not expose per-word confidence — which is why the
/// safety net flags whole utterances rather than individual digits.
/// </summary>
public sealed record WhisperSegment
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("start")]
    public double Start { get; init; }

    [JsonPropertyName("end")]
    public double End { get; init; }

    [JsonPropertyName("avg_logprob")]
    public double AvgLogprob { get; init; }

    [JsonPropertyName("no_speech_prob")]
    public double NoSpeechProb { get; init; }

    [JsonPropertyName("compression_ratio")]
    public double CompressionRatio { get; init; }
}

/// <summary>Raw shape of the Groq transcription response.</summary>
public sealed record TranscriptionResponse
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("segments")]
    public List<WhisperSegment> Segments { get; init; } = [];
}

/// <summary>
/// A single transcription pass, tagged with the model that produced it so
/// cross-pass disagreement can be reported meaningfully.
/// </summary>
public sealed record TranscriptionPass(
    string Model,
    string Text,
    IReadOnlyList<WhisperSegment> Segments);
