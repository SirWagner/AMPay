using System.ComponentModel.DataAnnotations;
using AMPay.Domain.Contracts;
using AMPay.Domain.Entities;
using AMPay.Domain.Enums;

namespace AMPay.Web.Models;

/// <summary>What the pack partial renders: the frozen snapshot, never live data.</summary>
public record ContractDocumentView(
    ContractSnapshot Snapshot,
    string Hash,
    ContractSignatureRecord? Signature);

/// <summary>The staff page for one pack.</summary>
public class ContractPageModel
{
    public required LoanContract Contract { get; init; }
    public required ContractDocumentView Document { get; init; }
    public required string LoanNumber { get; init; }
    public bool CanCapture { get; init; }
    public bool CanDecide { get; init; }
    public bool MessagesStubbed { get; init; }
    public IReadOnlyDictionary<string, string> UserNames { get; init; } = new Dictionary<string, string>();

    public string Who(string? id) => id is not null && UserNames.TryGetValue(id, out var n) ? n : "unknown";
}

/// <summary>The contract card on the loan page.</summary>
public class LoanContractsPanel
{
    public required Guid LoanId { get; init; }
    public required LoanStatus LoanStatus { get; init; }
    public required IReadOnlyList<LoanContract> Contracts { get; init; }
    public bool CanCapture { get; init; }
    public bool CanDecide { get; init; }

    /// <summary>The client has accepted a quote, so the agreement may be issued.</summary>
    public bool QuoteAccepted { get; init; }

    public LoanContract? Current => Contracts
        .Where(c => c.Status != ContractStatus.Voided)
        .OrderByDescending(c => c.Issue)
        .FirstOrDefault();
}

/// <summary>The client's signing page.</summary>
public class SignPageModel
{
    public required string Token { get; init; }
    public required string Reference { get; init; }
    public required ContractStatus Status { get; init; }
    public required ContractDocumentView Document { get; init; }
    public required string Lender { get; init; }
    public string? LenderContact { get; init; }
    public bool CanSignOnline { get; init; }
    public bool PinSent { get; init; }
    public string? MaskedMobile { get; init; }
    public bool Stubbed { get; init; }
    public string? Error { get; init; }
    public string? Notice { get; init; }
}

public class ContractTemplateEditModel
{
    public Guid Id { get; set; }
    public ContractTemplateKind Kind { get; set; }

    [Required, StringLength(200)]
    public string Title { get; set; } = "";

    [Required, StringLength(40000, MinimumLength = 20)]
    [Display(Name = "Wording")]
    public string Body { get; set; } = "";

    public bool IsApproved { get; set; }
    public DateTime? ApprovedUtc { get; set; }
    public string? ApprovedBy { get; set; }
}

/// <summary>The quote card on the loan page.</summary>
public class LoanQuotesPanel
{
    public required Guid LoanId { get; init; }
    public required LoanStatus LoanStatus { get; init; }
    public required IReadOnlyList<CreditQuote> Quotes { get; init; }
    public required string Lender { get; init; }
    public bool CanCapture { get; init; }
    public bool CanDecide { get; init; }
    public bool MessagesStubbed { get; init; }

    /// <summary>A contract has been signed on this loan: the quote can no longer change.</summary>
    public bool AgreementSigned { get; init; }

    public CreditQuote? Current => Quotes
        .Where(q => q.Status != QuoteStatus.Voided)
        .OrderByDescending(q => q.Issue)
        .FirstOrDefault();
}

/// <summary>The client's quote page.</summary>
public class QuotePageModel
{
    public required string Token { get; init; }
    public required CreditQuote Quote { get; init; }
    public required ContractSnapshot Snapshot { get; init; }
    public required string Lender { get; init; }
    public string? LenderContact { get; init; }
    public required IReadOnlyList<string> Consents { get; init; }
    public bool CanAcceptOnline { get; init; }
    public bool PinSent { get; init; }
    public string? MaskedMobile { get; init; }
    public bool Stubbed { get; init; }
    public string? Error { get; init; }
    public string? Notice { get; init; }
}
