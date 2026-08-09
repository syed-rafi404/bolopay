namespace BoloPay.Web.Models;

/// <summary>
/// Everything financial in this project is mocked. No accounts, no rails, no
/// persistence — the balance lives in browser state and resets on refresh.
/// </summary>
public sealed record MockContact(string Name, string BanglaName, string Phone);

public static class MockData
{
    public const decimal StartingBalance = 5000m;

    public static readonly MockContact Account =
        new("Rafi", "রাফি", "01711-XXXXXX");

    public static readonly IReadOnlyList<MockContact> Contacts =
    [
        new("Adiba", "আদিবা", "01911-XXXXXX"),
        new("Tanvir", "তানভির", "01611-XXXXXX"),
        new("Amma", "আম্মা", "01811-XXXXXX"),
    ];
}
