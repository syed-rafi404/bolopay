using BoloPay.Web.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BoloPay.Web.Pages;

public sealed record SampleClip(string File, string Label, string BanglaLabel, string Hint);

public class IndexModel(IWebHostEnvironment env) : PageModel
{
    public decimal StartingBalance => MockData.StartingBalance;

    public MockContact Account => MockData.Account;

    public IReadOnlyList<MockContact> Contacts => MockData.Contacts;

    /// <summary>
    /// The pre-recorded fallback path. Required, not optional: a visitor may
    /// not grant mic access, may be on a work laptop, or may not speak Bangla.
    ///
    /// Curated rather than enumerated from disk. wwwroot/sample-audio also holds
    /// calibration takes (03a, 03c, 03d) that are useful for measurement but
    /// would clutter the demo, and each entry needs a hand-written English label
    /// and Bangla transcript that no filename can supply.
    ///
    /// Entries whose file is missing are filtered out, so a clip can be removed
    /// without leaving a dead button.
    /// </summary>
    public IReadOnlyList<SampleClip> Samples =>
        AllSamples.Where(s => System.IO.File.Exists(
            Path.Combine(env.WebRootPath, "sample-audio", s.File))).ToArray();

    private static readonly SampleClip[] AllSamples =
    [
        new("01-clean-adiba-500.wav", "Send ৳500 to Adiba",
            "আদিবার নাম্বারে পাঁচশো টাকা পাঠাও", "clearly spoken"),
        new("02-clean-tanvir-900.wav", "Send ৳900 to Tanvir",
            "তানভিরকে নয়শো টাকা পাঠাও", "clearly spoken"),
        // Labelled by what the audio is, not by what the pipeline will do with
        // it. Calibration showed whisper-large-v3 recovers the amount from this
        // clip on every pass, so promising a flag here would be a claim the
        // demo cannot keep.
        new("03b-mumble-heavy.wav", "Mumbled amount",
            "আদিবার নাম্বারে পাঁচশো টাকা পাঠাও", "degraded speech"),
        new("08-poor-connection.wav", "Poor connection",
            "আদিবার নাম্বারে পাঁচশো টাকা পাঠাও", "noisy line"),
        // Measured, not assumed: this clip does not reach the unknown-recipient
        // screen. "রাকিব" is outside the prompt's seeded vocabulary, so Whisper
        // warps both the name and the amount and the command lands on the
        // unrecognised branch. Labelled for what it actually demonstrates —
        // failing closed on speech the pipeline cannot parse.
        new("06-unknown-recipient.wav", "Name outside contacts",
            "রাকিবকে তিনশো টাকা পাঠাও", "not understood"),
        new("07-over-balance.wav", "More than the balance",
            "আম্মাকে পঞ্চাশ হাজার টাকা পাঠাও", "blocked"),
        new("04-balance.wav", "Check balance",
            "আমার ব্যালেন্স কত?", ""),
        new("05-nonsense.wav", "Off-topic speech",
            "আজকে আবহাওয়া খুব সুন্দর", "not a command"),
    ];
}
