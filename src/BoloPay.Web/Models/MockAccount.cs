namespace BoloPay.Web.Models;

/// <summary>
/// Everything financial in this project is mocked. No accounts, no rails, no
/// persistence — the balance lives in browser state and resets on refresh.
/// </summary>
public sealed record MockContact(string Name, string BanglaName, string Phone);

public static class MockData
{
    public const decimal StartingBalance = 5000m;

    // Banglalink's operator prefixes are 019 and 014 (BTRC national numbering
    // plan). Avoid 017/013 (Grameenphone), 016/018 (Robi/Airtel) and 015
    // (Teletalk): showing a competitor's prefix in a Banglalink-facing demo is
    // the kind of detail an interviewer notices. Subscriber digits are masked
    // so nothing here resembles a real, dialable number.
    public static readonly MockContact Account =
        new("Rafi", "রাফি", "01911-XXXXXX");

    public static readonly IReadOnlyList<MockContact> Contacts =
    [
        new("Adiba", "আদিবা", "01914-XXXXXX"),
        new("Tanvir", "তানভির", "01412-XXXXXX"),
        new("Amma", "আম্মা", "01913-XXXXXX"),
    ];
}
