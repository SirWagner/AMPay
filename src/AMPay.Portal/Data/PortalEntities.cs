using AMPay.Domain.Enums;
using AMPay.Domain.Portal;

namespace AMPay.Portal.Data;

/// <summary>
/// A lender as the portal knows it: only what a member of the public may see, plus the
/// packages used for estimates. Written by AM-Pay through the API, never edited here.
/// </summary>
public class Lender
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The code in the link: /a/{PublicCode}.</summary>
    public string PublicCode { get; set; } = "";

    /// <summary>The AM-Pay tenant this lender is. Opaque to the portal; echoed back to AM-Pay.</summary>
    public Guid AmpayTenantId { get; set; }

    public string Name { get; set; } = "";
    public string? TradingName { get; set; }
    public string? NcrNumber { get; set; }
    public string? ContactNumber { get; set; }
    public string? ContactEmail { get; set; }
    public string? WhatsAppNumber { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>The lender's packages as JSON (PortalPackage[]), for estimates only.</summary>
    public string PackagesJson { get; set; } = "[]";

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public string DisplayName => string.IsNullOrWhiteSpace(TradingName) ? Name : TradingName;
}

/// <summary>A person who proved they hold a cell number, for one lender.</summary>
public class Applicant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LenderId { get; set; }
    public Lender? Lender { get; set; }

    /// <summary>27XXXXXXXXX.</summary>
    public string Mobile { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSignInUtc { get; set; }

    public ICollection<LoanApplication> Applications { get; set; } = new List<LoanApplication>();
}

public class LoanApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LenderId { get; set; }
    public Lender? Lender { get; set; }
    public Guid ApplicantId { get; set; }
    public Applicant? Applicant { get; set; }

    /// <summary>What the client quotes on the phone: APP-7K2Q9M.</summary>
    public string Reference { get; set; } = "";
    public PortalApplicationStatus Status { get; set; } = PortalApplicationStatus.Draft;

    // About you
    public string? Title { get; set; }
    public string? FirstName { get; set; }
    public string? MiddleNames { get; set; }
    public string? Surname { get; set; }
    public string? IdNumber { get; set; }
    public string? Email { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }

    // Work and income
    public string? EmployerName { get; set; }
    public string? Occupation { get; set; }
    public DateTime? EmployedSince { get; set; }
    public string? PayFrequency { get; set; }
    public int? SalaryDay { get; set; }
    public decimal? GrossMonthlyIncome { get; set; }
    public decimal? NetMonthlyIncome { get; set; }
    public decimal? OtherMonthlyIncome { get; set; }
    public decimal? MonthlyExpenses { get; set; }
    public decimal? MonthlyDebtRepayments { get; set; }

    // The loan asked for
    public decimal? RequestedAmount { get; set; }
    public int? RequestedTerm { get; set; }
    public string? PackageName { get; set; }
    public decimal? EstimatedInstalment { get; set; }

    // Consent, recorded at submission
    public bool DataProcessingConsent { get; set; }
    public bool CreditCheckConsent { get; set; }
    public bool MarketingConsent { get; set; }
    public DateTime? ConsentUtc { get; set; }
    public string? ConsentIp { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedUtc { get; set; }
    public DateTime? PickedUpUtc { get; set; }
    public Guid? AmpayClientId { get; set; }
    public string? AmpayClientNumber { get; set; }

    public ICollection<ApplicationDocument> Documents { get; set; } = new List<ApplicationDocument>();

    public bool IsEditable => Status == PortalApplicationStatus.Draft;
}

public class ApplicationDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public LoanApplication? Application { get; set; }

    public DocumentType DocumentType { get; set; }
    public string FileName { get; set; } = "";
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>Relative to the portal's document root. Never served by URL.</summary>
    public string StoragePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTime UploadedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>"Talk to an advisor": a request for the lender to phone back.</summary>
public class CallbackRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LenderId { get; set; }
    public Lender? Lender { get; set; }
    public Guid? ApplicationId { get; set; }

    public string Name { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string? PreferredTime { get; set; }
    public string? Message { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? RequestIp { get; set; }
    public DateTime? HandledUtc { get; set; }
}

/// <summary>A one-time sign-in code. Only its hash is kept.</summary>
public class OtpChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LenderId { get; set; }
    public string Mobile { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public int FailedAttempts { get; set; }
    public DateTime? ConsumedUtc { get; set; }
    public string? RequestIp { get; set; }
}
