using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using BoloPay.Web.Models;
using Microsoft.Extensions.Options;

namespace BoloPay.Web.Services;

/// <summary>
/// Groq speech-to-text. Uses verbose_json with segment granularity because
/// the confidence fields the safety net depends on exist nowhere else —
/// word-level granularity returns timings only, no per-word probability.
/// </summary>
public sealed class GroqTranscriptionService(
    HttpClient http,
    IOptions<GroqOptions> options,
    ILogger<GroqTranscriptionService> logger) : ITranscriptionService
{
    private readonly GroqOptions _options = options.Value;

    public async Task<TranscriptionPass> TranscribeAsync(
        Stream audio,
        string fileName,
        string contentType,
        string model,
        float temperature = 0f,
        CancellationToken cancellationToken = default)
    {
        // Buffer into a byte array rather than wrapping the caller's stream in
        // StreamContent. MultipartFormDataContent disposes its children, which
        // would dispose a stream this method does not own — and the pipeline
        // deliberately reuses one stream across both ASR passes. ByteArrayContent
        // makes that cascade harmless.
        var audioBytes = await ReadAllBytesAsync(audio, cancellationToken);

        using var form = new MultipartFormDataContent();

        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);

        form.Add(audioContent, "file", fileName);
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(_options.Language), "language");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");
        // InvariantCulture matters: on a machine with a comma decimal separator
        // this would otherwise send "0,4" and the API would reject it.
        form.Add(
            new StringContent(temperature.ToString(CultureInfo.InvariantCulture)),
            "temperature");

        // Whisper's prompt parameter (max 224 tokens) steers vocabulary and
        // spelling. Seeding it with the contact names and money phrasing this
        // demo expects measurably helps on exactly the two fields that matter.
        form.Add(new StringContent(BuildPrompt()), "prompt");

        using var response = await http.PostAsync("audio/transcriptions", form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Groq ASR failed ({Status}) for model {Model}: {Body}",
                (int)response.StatusCode, model, body);

            throw new GroqException(
                $"Transcription failed with status {(int)response.StatusCode}.",
                response.StatusCode);
        }

        var parsed = JsonSerializer.Deserialize<TranscriptionResponse>(body)
                     ?? throw new GroqException("Transcription returned an unreadable response.");

        logger.LogInformation(
            "ASR pass [{Model} @ t={Temperature}] -> {SegmentCount} segment(s): {Text}",
            model, temperature, parsed.Segments.Count, parsed.Text);

        foreach (var s in parsed.Segments)
        {
            // Logged at Information so Phase 3 calibration can read real values
            // straight from the console instead of guessing thresholds.
            logger.LogInformation(
                "  segment {Id}: avg_logprob={AvgLogprob:F4} no_speech_prob={NoSpeech:F4} compression_ratio={Compression:F4} text=\"{Text}\"",
                s.Id, s.AvgLogprob, s.NoSpeechProb, s.CompressionRatio, s.Text.Trim());
        }

        return new TranscriptionPass(model, parsed.Text.Trim(), parsed.Segments);
    }

    private static async Task<byte[]> ReadAllBytesAsync(
        Stream audio,
        CancellationToken cancellationToken)
    {
        // Avoid a copy when the caller already handed over a MemoryStream,
        // which is the pipeline's normal path.
        if (audio is MemoryStream ms)
            return ms.ToArray();

        using var copy = new MemoryStream();
        await audio.CopyToAsync(copy, cancellationToken);
        return copy.ToArray();
    }

    private string BuildPrompt()
    {
        // Whisper's prompt (max 224 tokens) biases vocabulary and spelling.
        // The larger amounts are listed explicitly because calibration showed
        // "পঞ্চাশ হাজার" being heard as "পন্ছশে", which dropped the amount
        // entirely and sent a valid command to the unrecognized branch.
        //
        // Contact names are seeded by default. Dropping them was tried and
        // measured worse on every axis: known-contact accuracy fell from 12/12
        // to 6/15, and amounts degraded too — "পঞ্চাশ হাজার" (50,000) was read
        // as "পঞ্চাশ" (50). It also failed to fix the unfamiliar-name case that
        // motivated the change, because the placeholder names in the examples
        // just became the new bias.
        //
        // The trade-off is inherent to prompting: biasing toward a vocabulary
        // helps inside it and hurts outside it. Since recipients here always
        // come from the contact list, biasing toward that list is correct, and
        // an unknown name should fail closed rather than be guessed at.
        var header = _options.IncludeContactNamesInPrompt
            ? "বিকাশ/মোবাইল ব্যাংকিং কমান্ড। পরিচিত নাম: "
              + string.Join(", ", MockData.Contacts.Select(c => $"{c.BanglaName} ({c.Name})"))
              + "। "
            : "বিকাশ/মোবাইল ব্যাংকিং কমান্ড। ";

        return header
            + "টাকার পরিমাণ: একশো, দুইশো, তিনশো, চারশো, পাঁচশো, ছয়শো, সাতশো, আটশো, নয়শো, "
            + "এক হাজার, দুই হাজার, পাঁচ হাজার, দশ হাজার, বিশ হাজার, পঞ্চাশ হাজার। "
            + "উদাহরণ: আদিবার নাম্বারে পাঁচশো টাকা পাঠাও। "
            + "আম্মাকে পঞ্চাশ হাজার টাকা পাঠাও। আমার ব্যালেন্স কত?";
    }
}

public sealed class GroqException(string message, System.Net.HttpStatusCode? statusCode = null)
    : Exception(message)
{
    public System.Net.HttpStatusCode? StatusCode { get; } = statusCode;
}
