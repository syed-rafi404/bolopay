using BoloPay.Web.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BoloPay.Web.Pages;

public sealed record SampleClip(string File, string Label, string BanglaLabel, string Hint);

public class IndexModel : PageModel
{
    public decimal StartingBalance => MockData.StartingBalance;

    public MockContact Account => MockData.Account;

    public IReadOnlyList<MockContact> Contacts => MockData.Contacts;

    /// <summary>
    /// The pre-recorded fallback path. Required, not optional: a visitor may
    /// not grant mic access, may be on a work laptop, or may not speak Bangla.
    /// Files are dropped into wwwroot/sample-audio by hand.
    /// </summary>
    public IReadOnlyList<SampleClip> Samples =>
    [
        new("01-clean-adiba-500.wav", "Send ৳500 to Adiba",
            "আদিবার নাম্বারে পাঁচশো টাকা পাঠাও", "clearly spoken"),
        new("02-clean-tanvir-900.wav", "Send ৳900 to Tanvir",
            "তানভিরকে নয়শো টাকা পাঠাও", "clearly spoken"),
        new("03b-mumble-heavy.wav", "Unclear amount",
            "আদিবার নাম্বারে পাঁচশো টাকা পাঠাও", "mumbled — should get flagged"),
        new("04-balance.wav", "Check balance",
            "আমার ব্যালেন্স কত?", ""),
        new("05-nonsense.wav", "Off-topic speech",
            "আজকে আবহাওয়া খুব সুন্দর", "not a command"),
        new("07-over-balance.wav", "More than the balance",
            "আম্মাকে পঞ্চাশ হাজার টাকা পাঠাও", "blocked"),
    ];
}
