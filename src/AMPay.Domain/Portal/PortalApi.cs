using AMPay.Domain.Enums;

namespace AMPay.Domain.Portal;

/// <summary>
/// The contract between AM-Pay and the self-service portal.
/// <para>
/// Every call goes one way: AM-Pay calls the portal. The portal holds no address for AM-Pay
/// and no credential to it, so a compromised portal cannot reach the lender's book. AM-Pay
/// pushes each lender's public details and packages, and pulls submitted applications,
/// their documents and callback requests.
/// </para>
/// <para>
/// Both sides compile against these records, so a change here is a change to both.
/// </para>
/// </summary>
public static class PortalApi
{
    /// <summary>Header carrying the shared API key.</summary>
    public const string ApiKeyHeader = "X-AMPay-Portal-Key";

    public const string LendersPath = "api/v1/lenders";
    public const string ApplicationsPath = "api/v1/applications";
    public const string CallbacksPath = "api/v1/callbacks";

    /// <summary>
    /// A lender's short public code: six characters from an alphabet without look-alikes,
    /// so it survives being read out or typed from a printed flyer.
    /// </summary>
    public const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 6;

    public static string NewLenderCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = CodeAlphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars);
    }

    public static bool IsValidLenderCode(string? code) =>
        code is { Length: CodeLength } && code.All(c => CodeAlphabet.Contains(c));

    /// <summary>
    /// South African cell numbers in one form, 27XXXXXXXXX, whatever was typed:
    /// 082 123 4567, +27 82 123 4567, 27821234567. Null when it is not a cell number.
    /// </summary>
    public static string? NormaliseMobile(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = new string(input.Where(char.IsDigit).ToArray());

        if (digits.Length == 10 && digits[0] == '0') digits = "27" + digits[1..];
        if (digits.Length != 11 || !digits.StartsWith("27")) return null;

        // Cell numbers start 06, 07 or 08 nationally.
        return digits[2] is '6' or '7' or '8' ? digits : null;
    }

    /// <summary>27821234567 as 082 123 4567, the way people read their own number.</summary>
    public static string DisplayMobile(string normalised) =>
        normalised.Length == 11 ? $"0{normalised[2..4]} {normalised[4..7]} {normalised[7..]}" : normalised;
}

public enum PortalApplicationStatus
{
    /// <summary>The client is still filling it in.</summary>
    Draft = 0,
    /// <summary>Sent to the lender; waiting to be picked up in AM-Pay.</summary>
    Submitted = 1,
    /// <summary>Imported into AM-Pay; onboarding continues there.</summary>
    PickedUp = 2,
    Withdrawn = 3
}

// ------------------------------------------------------------------ AM-Pay -> portal

public record PortalLenderSync(
    Guid TenantId,
    string Name,
    string? TradingName,
    string? NcrNumber,
    string? ContactNumber,
    string? ContactEmail,
    string? WhatsAppNumber,
    bool IsActive,
    IReadOnlyList<PortalPackage> Packages);

public record PortalPackage(
    string Name,
    CreditTier Tier,
    decimal MonthlyInterestRate,
    decimal MonthlyServiceFee,
    decimal InitiationFeeRate,
    decimal CreditLifeRate,
    decimal MinLoanAmount,
    decimal MaxLoanAmount,
    int MinTermMonths,
    int MaxTermMonths);

public record PortalPickedUp(Guid AmpayClientId, string ClientNumber);

// ------------------------------------------------------------------ portal -> AM-Pay (responses)

public record PortalApplicationSummary(
    Guid Id,
    string Reference,
    Guid TenantId,
    PortalApplicationStatus Status,
    string? FirstName,
    string? Surname,
    string? IdNumber,
    string Mobile,
    decimal? RequestedAmount,
    int? RequestedTerm,
    int DocumentCount,
    DateTime CreatedUtc,
    DateTime? SubmittedUtc,
    DateTime? PickedUpUtc,
    Guid? AmpayClientId);

public record PortalApplicationDetail(
    PortalApplicationSummary Summary,
    string? Title,
    string? MiddleNames,
    string? Email,
    string? AddressLine1,
    string? AddressLine2,
    string? Suburb,
    string? City,
    string? Province,
    string? PostalCode,
    string? EmployerName,
    string? Occupation,
    DateTime? EmployedSince,
    string? PayFrequency,
    int? SalaryDay,
    decimal? GrossMonthlyIncome,
    decimal? NetMonthlyIncome,
    decimal? OtherMonthlyIncome,
    decimal? MonthlyExpenses,
    decimal? MonthlyDebtRepayments,
    string? PackageName,
    decimal? EstimatedInstalment,
    bool DataProcessingConsent,
    bool CreditCheckConsent,
    bool MarketingConsent,
    DateTime? ConsentUtc,
    IReadOnlyList<PortalDocument> Documents);

public record PortalDocument(
    Guid Id,
    DocumentType DocumentType,
    string FileName,
    string? ContentType,
    long SizeBytes,
    string Sha256,
    DateTime UploadedUtc);

public record PortalCallback(
    Guid Id,
    Guid TenantId,
    Guid? ApplicationId,
    string Name,
    string Mobile,
    string? PreferredTime,
    string? Message,
    DateTime CreatedUtc,
    DateTime? HandledUtc);
