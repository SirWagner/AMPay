using System.Text.RegularExpressions;

namespace AMPay.Domain.Contracts;

/// <summary>
/// The fields a lender's wording may refer to, written <c>{{Lender.RegisteredName}}</c>. They
/// are filled in when a contract is issued and frozen with it, so one set of wording serves
/// every client and every lender without a name or a rate typed into the legal text.
/// </summary>
public static partial class ContractMergeFields
{
    /// <summary>Every field that can be used, with what it prints. Shown beside the editor.</summary>
    public static readonly IReadOnlyList<(string Key, string Meaning)> All = new[]
    {
        ("Lender.RegisteredName", "The credit provider's registered name"),
        ("Lender.TradingName", "The name the credit provider trades under"),
        ("Lender.NcrRegNo", "NCR registration number"),
        ("Lender.ContactNo", "The credit provider's contact number"),
        ("Client.FullNames", "The borrower's first and middle names"),
        ("Client.Surname", "The borrower's surname"),
        ("Client.Number", "Client number"),
        ("Client.Language", "The borrower's preferred language"),
        ("Loan.ContractNo", "The agreement reference"),
        ("Cost.Principal", "The loan amount paid out"),
        ("Cost.TotalRepayable", "Total amount repayable"),
        ("Rate.MonthlyPct", "Monthly interest rate"),
        ("Rate.AnnualPct", "Annual interest rate"),
        ("Rate.PenaltyMonthlyPct", "Monthly rate on arrears"),
        ("Schedule.InstalmentCount", "Number of instalments"),
        ("Schedule.InstalmentAmount", "Instalment amount"),
        ("Schedule.PaymentMethod", "How the instalments are collected")
    };

    private static readonly HashSet<string> Known =
        All.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\{\{\s*([^{}]*?)\s*\}\}")]
    private static partial Regex Field();

    /// <summary>Fields in the text that do not exist. Wording with any of these must not be saved.</summary>
    public static IReadOnlyList<string> Unknown(string? text) =>
        Field().Matches(text ?? "")
            .Select(m => m.Groups[1].Value)
            .Where(k => !Known.Contains(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The text with every known field replaced by its value. A field with no value prints as
    /// nothing rather than "None" or "null"; an unknown field is left as written, so a mistake is
    /// visible on the page rather than silently dropped.
    /// </summary>
    public static string Resolve(string text, IReadOnlyDictionary<string, string?> values) =>
        Field().Replace(text, m =>
        {
            var key = m.Groups[1].Value;
            return Known.Contains(key)
                ? values.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)).Value ?? ""
                : m.Value;
        });
}
