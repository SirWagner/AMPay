using System.ComponentModel.DataAnnotations;
using AMPay.Domain.Enums;

namespace AMPay.Web.Models;

/// <summary>One labelled value in a chart.</summary>
public record ChartSlice(string Label, decimal Value, string? Colour = null);

public class DashboardViewModel
{
    public bool IsPlatformUser { get; set; }
    public string? TenantName { get; set; }

    public int CustomerCount { get; set; }
    public int ClientCount { get; set; }
    public int AuthenticatedMandates { get; set; }
    public int PendingMandates { get; set; }
    public int RejectedMandates { get; set; }
    public decimal MonthlyCollectionValue { get; set; }
    public decimal PayNowCollected { get; set; }

    // ---- Client lifecycle ----

    /// <summary>Headline counts, split so that captured is never mistaken for lendable.</summary>
    public int CapturedClients { get; set; }
    public int AwaitingVerification { get; set; }
    public int OnboardedClients { get; set; }
    public int ActiveClients { get; set; }

    // ---- Loan book ----

    public int LoansInApproval { get; set; }
    public int DisbursedLoans { get; set; }
    public decimal LoanBookPrincipal { get; set; }
    public decimal LoanBookOutstanding { get; set; }

    // ---- Charts ----

    /// <summary>Onboarding funnel, in lifecycle order.</summary>
    public List<ChartSlice> Pipeline { get; set; } = new();

    /// <summary>Mandates by status.</summary>
    public List<ChartSlice> MandateBreakdown { get; set; } = new();

    /// <summary>Clients captured per month over the last twelve months.</summary>
    public List<ChartSlice> ClientsByMonth { get; set; } = new();

    /// <summary>Value advanced per credit package.</summary>
    public List<ChartSlice> LoanBookByPackage { get; set; } = new();

    /// <summary>True when there is nothing yet to chart - drives the empty state.</summary>
    public bool HasLoanBook => LoanBookByPackage.Any(s => s.Value > 0);

    public List<ClientRow> RecentClients { get; set; } = new();
    public List<MandateRow> RecentMandates { get; set; } = new();

    public class ClientRow
    {
        public Guid Id { get; set; }
        public string ClientNumber { get; set; } = "";
        public string Name { get; set; } = "";
        public ClientStatus Status { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public class MandateRow
    {
        public Guid Id { get; set; }
        public string AccountReference { get; set; } = "";
        public string ClientName { get; set; } = "";
        public decimal Amount { get; set; }
        public MandateStatus Status { get; set; }
        public MandateType MandateType { get; set; }
    }
}

/// <summary>
/// The onboarding wizard steps, in the order Maxmoney captures them.
/// </summary>
public enum OnboardingStep
{
    General = 1,
    Employment = 2,
    Financial = 3,
    Banking = 4,
    Payback = 5,
    Address = 6,
    OtherDetails = 7,
    References = 8,
    Budgets = 9,
    CreditCheck = 10,
    Notes = 11,
    Documents = 12,
    Photo = 13
}

public static class OnboardingSteps
{
    public static readonly IReadOnlyList<(OnboardingStep Step, string Label, string Action)> All = new[]
    {
        (OnboardingStep.General,      "General",       "General"),
        (OnboardingStep.Employment,   "Employment",    "Employment"),
        (OnboardingStep.Financial,    "Financial",     "Financial"),
        (OnboardingStep.Banking,      "Banking",       "Banking"),
        (OnboardingStep.Payback,      "Payback",       "Payback"),
        (OnboardingStep.Address,      "Address",       "Address"),
        (OnboardingStep.OtherDetails, "Other details", "OtherDetails"),
        (OnboardingStep.References,   "References",    "References"),
        (OnboardingStep.Budgets,      "Budgets",       "Budgets"),
        (OnboardingStep.CreditCheck,  "Credit check",  "CreditCheck"),
        (OnboardingStep.Notes,        "Notes",         "Notes"),
        (OnboardingStep.Documents,    "Documents",     "Documents"),
        (OnboardingStep.Photo,        "Photograph",    "Photo")
    };
}

/// <summary>Shared chrome for every wizard step.</summary>
public class WizardContext
{
    public Guid ClientId { get; set; }
    public string ClientNumber { get; set; } = "";
    public string ClientName { get; set; } = "";
    public OnboardingStep CurrentStep { get; set; }
    public HashSet<OnboardingStep> CompletedSteps { get; set; } = new();
}

public class GeneralStepModel
{
    public Guid? ClientId { get; set; }

    [Display(Name = "Client number")]
    [StringLength(32)]
    public string? ClientNumber { get; set; }

    [StringLength(20)]
    public string? Title { get; set; }

    [Required, StringLength(100), Display(Name = "First name")]
    public string FirstName { get; set; } = "";

    [StringLength(100), Display(Name = "Middle names")]
    public string? MiddleNames { get; set; }

    [Required, StringLength(100)]
    public string Surname { get; set; } = "";

    [Display(Name = "Identifies with an SA ID number")]
    public bool IsSaIdNumber { get; set; } = true;

    [Required, StringLength(20), Display(Name = "ID / passport number")]
    public string IdNumber { get; set; } = "";

    [Display(Name = "Date of birth"), DataType(DataType.Date)]
    public DateTime? DateOfBirth { get; set; }

    public string? Gender { get; set; }

    [Display(Name = "Marital status")]
    public string? MaritalStatus { get; set; }

    public string? Nationality { get; set; } = "South African";

    [Display(Name = "Preferred language")]
    public string? PreferredLanguage { get; set; }
}

public class EmploymentStepModel
{
    public Guid ClientId { get; set; }

    [Display(Name = "Employer name"), StringLength(200)]
    public string? EmployerName { get; set; }

    [StringLength(100)]
    public string? Occupation { get; set; }

    [StringLength(100)]
    public string? Department { get; set; }

    [Display(Name = "Employee number"), StringLength(50)]
    public string? EmployeeNumber { get; set; }

    [Display(Name = "Employment type")]
    public string? EmploymentType { get; set; }

    [Display(Name = "Employed since"), DataType(DataType.Date)]
    public DateTime? EmployedSince { get; set; }

    [Display(Name = "Work telephone"), StringLength(20)]
    public string? WorkTelephone { get; set; }

    [Display(Name = "Supervisor name"), StringLength(200)]
    public string? SupervisorName { get; set; }

    [Display(Name = "Pay frequency")]
    public string? PayFrequency { get; set; }

    [Display(Name = "Salary day of month"), Range(1, 31)]
    public int? SalaryDay { get; set; }
}

public class FinancialStepModel
{
    public Guid ClientId { get; set; }

    [Display(Name = "Gross monthly income"), Range(0, 10_000_000)]
    public decimal? GrossMonthlyIncome { get; set; }

    [Display(Name = "Net monthly income"), Range(0, 10_000_000)]
    public decimal? NetMonthlyIncome { get; set; }

    [Display(Name = "Other income"), Range(0, 10_000_000)]
    public decimal? OtherIncome { get; set; }

    [Display(Name = "Other income source")]
    public string? OtherIncomeSource { get; set; }

    [Display(Name = "Total monthly expenses"), Range(0, 10_000_000)]
    public decimal? TotalMonthlyExpenses { get; set; }

    [Display(Name = "Total monthly debt repayments"), Range(0, 10_000_000)]
    public decimal? TotalMonthlyDebtRepayments { get; set; }

    [Display(Name = "Bank")]
    public string? BankName { get; set; }

    [Display(Name = "Years at bank"), Range(0, 80)]
    public int? YearsAtBank { get; set; }

    public decimal? Disposable =>
        NetMonthlyIncome is null ? null
        : NetMonthlyIncome - (TotalMonthlyExpenses ?? 0m) - (TotalMonthlyDebtRepayments ?? 0m);
}

public class BankAccountModel
{
    public Guid? Id { get; set; }
    public Guid ClientId { get; set; }

    [Required, StringLength(30), Display(Name = "Account holder name")]
    public string AccountHolderName { get; set; } = "";

    [Required, Display(Name = "Bank")]
    public string BankName { get; set; } = "";

    [Required, StringLength(6, MinimumLength = 6), Display(Name = "Branch code")]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Branch code must be exactly six digits.")]
    public string BranchCode { get; set; } = "";

    [Required, StringLength(16), Display(Name = "Account number")]
    [RegularExpression(@"^\d{6,16}$", ErrorMessage = "Account number must be 6 to 16 digits.")]
    public string AccountNumber { get; set; } = "";

    [Display(Name = "Account type")]
    public BankAccountType AccountType { get; set; } = BankAccountType.Cheque;

    [Display(Name = "Primary account for collections")]
    public bool IsPrimary { get; set; } = true;
}

public class PaybackStepModel
{
    public Guid ClientId { get; set; }

    [Display(Name = "Loan amount"), Range(0, 10_000_000)]
    public decimal? LoanAmount { get; set; }

    [Display(Name = "Instalment amount"), Range(0, 10_000_000)]
    public decimal? InstalmentAmount { get; set; }

    [Display(Name = "Number of instalments"), Range(1, 600)]
    public int? NumberOfInstalments { get; set; }

    [Display(Name = "Collection frequency")]
    public DebitFrequency Frequency { get; set; } = DebitFrequency.Monthly;

    [Display(Name = "Collection day")]
    public string? CollectionDay { get; set; }

    [Display(Name = "Netcash collection day code"), StringLength(7)]
    public string? CollectionDayCode { get; set; }

    [Display(Name = "First collection date"), DataType(DataType.Date)]
    public DateTime? FirstCollectionDate { get; set; }

    [Display(Name = "First collection differs from the rest")]
    public bool FirstCollectionDiffers { get; set; }

    [Display(Name = "First collection amount"), Range(0, 10_000_000)]
    public decimal? FirstCollectionAmount { get; set; }

    [Display(Name = "DebiCheck tracking days"), Range(1, 10)]
    public int TrackingDays { get; set; } = 5;

    [Display(Name = "Agreement date"), DataType(DataType.Date)]
    public DateTime? AgreementDate { get; set; } = DateTime.UtcNow.Date;
}

public class AddressStepModel
{
    public Guid ClientId { get; set; }
    public Guid? Id { get; set; }

    [Display(Name = "Address type")]
    public AddressType AddressType { get; set; } = AddressType.Physical;

    [Display(Name = "Address line 1"), StringLength(200)]
    public string? Line1 { get; set; }

    [Display(Name = "Address line 2"), StringLength(200)]
    public string? Line2 { get; set; }

    [StringLength(100)]
    public string? Suburb { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    [StringLength(100)]
    public string? Province { get; set; }

    [Display(Name = "Postal code"), StringLength(10)]
    public string? PostalCode { get; set; }

    [Display(Name = "Home telephone"), StringLength(20)]
    public string? HomeTelephone { get; set; }

    [Display(Name = "Mobile number"), StringLength(20)]
    [RegularExpression(@"^0\d{9}$", ErrorMessage = "Enter a ten digit mobile number starting with 0, e.g. 0825551234.")]
    public string? MobileNumber { get; set; }

    [Display(Name = "Email address"), EmailAddress]
    public string? EmailAddress { get; set; }
}

public class OtherDetailsStepModel
{
    public Guid ClientId { get; set; }

    [Display(Name = "Risk category")]
    public string? RiskCategory { get; set; }

    [Display(Name = "Client category")]
    public string? ClientCategory { get; set; }

    [Display(Name = "Lead source")]
    public string? Source { get; set; }

    [Display(Name = "Consents to processing of personal information (POPIA)")]
    public bool DataProcessingConsent { get; set; }

    [Display(Name = "Consents to a credit bureau enquiry")]
    public bool CreditCheckConsent { get; set; }

    [Display(Name = "Consents to direct marketing")]
    public bool MarketingConsent { get; set; }

    public string? Comments { get; set; }
}

public class CreateMandateModel
{
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string ClientNumber { get; set; } = "";

    [Display(Name = "Authentication route")]
    public MandateType MandateType { get; set; } = MandateType.DebiCheckTt1RealTime;

    [Required, Display(Name = "Account reference"), StringLength(32)]
    public string AccountReference { get; set; } = "";

    [Display(Name = "Mandate template"), StringLength(14)]
    public string? MandateTemplateId { get; set; }

    [Required, Display(Name = "Collection amount"), Range(0.01, 10_000_000)]
    public decimal CollectionAmount { get; set; }

    [Display(Name = "First collection differs")]
    public bool FirstCollectionDiffers { get; set; }

    [Display(Name = "First collection amount"), Range(0, 10_000_000)]
    public decimal? FirstCollectionAmount { get; set; }

    [Display(Name = "First collection date"), DataType(DataType.Date)]
    public DateTime? FirstCollectionDate { get; set; }

    [Display(Name = "Frequency")]
    public DebitFrequency Frequency { get; set; } = DebitFrequency.Monthly;

    [Display(Name = "Collection day code"), StringLength(7)]
    public string? CollectionDayCode { get; set; }

    [Display(Name = "Tracking days"), Range(1, 10)]
    public int TrackingDays { get; set; } = 5;

    [Display(Name = "Bank account")]
    public Guid? BankAccountId { get; set; }

    public List<BankAccountOption> BankAccounts { get; set; } = new();
    public List<string> AvailableTemplates { get; set; } = new();

    public class BankAccountOption
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = "";
    }
}

public class CheckoutModel
{
    [Required, Display(Name = "Amount"), Range(1.00, 1_000_000)]
    public decimal Amount { get; set; }

    [Required, Display(Name = "What is this payment for"), StringLength(200)]
    public string Description { get; set; } = "";

    [Display(Name = "Customer name"), StringLength(200)]
    public string? CustomerName { get; set; }

    [Display(Name = "Customer email"), EmailAddress]
    public string? CustomerEmail { get; set; }

    [Display(Name = "Customer mobile"), StringLength(20)]
    public string? CustomerMobile { get; set; }

    public Guid? ClientId { get; set; }
}

public class TenantModel
{
    public Guid? Id { get; set; }

    [Required, StringLength(200), Display(Name = "Registered name")]
    public string Name { get; set; } = "";

    [StringLength(200), Display(Name = "Trading name")]
    public string? TradingName { get; set; }

    [StringLength(50), Display(Name = "Company registration number")]
    public string? RegistrationNumber { get; set; }

    [StringLength(50), Display(Name = "NCR number")]
    public string? NcrNumber { get; set; }

    [StringLength(11), Display(Name = "Netcash account number")]
    [RegularExpression(@"^5\d{10}$", ErrorMessage = "A Netcash account number is eleven digits starting with 5.")]
    public string? NetcashAccountNumber { get; set; }

    [EmailAddress, Display(Name = "Contact email")]
    public string? ContactEmail { get; set; }

    [Display(Name = "Contact number"), StringLength(20)]
    public string? ContactNumber { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Onboarding;
}

public class CreateUserModel
{
    [Required, EmailAddress, Display(Name = "Email address")]
    public string Email { get; set; } = "";

    [Required, StringLength(200), Display(Name = "Full name")]
    public string FullName { get; set; } = "";

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 12)]
    [Display(Name = "Temporary password")]
    public string Password { get; set; } = "";

    [Required, Display(Name = "Role")]
    public string Role { get; set; } = AMPay.Infrastructure.Identity.AppRoles.Capturer;

    [Display(Name = "Customer account")]
    public Guid? TenantId { get; set; }

    public List<(Guid Id, string Name)> Tenants { get; set; } = new();
}

// -------------------------------------------------------------------------------------
// Supporting documents
// -------------------------------------------------------------------------------------

/// <summary>One row of the reviewer's queue.</summary>
public class DocumentQueueRow
{
    public Guid DocumentId { get; set; }
    public Guid ClientId { get; set; }
    public string ClientNumber { get; set; } = "";
    public string ClientName { get; set; } = "";
    public DocumentType DocumentType { get; set; }
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime UploadedUtc { get; set; }

    public string SizeDisplay => SizeBytes < 1024 * 1024
        ? $"{SizeBytes / 1024d:N0} KB"
        : $"{SizeBytes / 1024d / 1024d:N1} MB";

    /// <summary>How long this has been waiting. A queue without ageing is not a queue.</summary>
    public string Waiting
    {
        get
        {
            var age = DateTime.UtcNow - UploadedUtc;
            if (age.TotalHours < 1) return $"{Math.Max(1, (int)age.TotalMinutes)} min";
            if (age.TotalDays < 1) return $"{(int)age.TotalHours} hr";
            return $"{(int)age.TotalDays} d";
        }
    }
}

/// <summary>The documents tab for one client.</summary>
public class ClientDocumentsModel
{
    public Guid ClientId { get; set; }
    public string ClientNumber { get; set; } = "";
    public string ClientName { get; set; } = "";
    public ClientStatus ClientStatus { get; set; }

    /// <summary>True when the signed-in user may accept or reject.</summary>
    public bool CanReview { get; set; }

    /// <summary>
    /// True when the user can also reach the full client file. A reviewer cannot, so this
    /// page has to carry everything they need on its own.
    /// </summary>
    public bool CanOpenClientFile { get; set; }

    // ---- What the reviewer checks the documents against ----
    // Verification is a comparison: the name and number on the identity document against
    // what was captured, the employer and income on the payslip against the Financial tab.
    // Without these on the page the reviewer is just confirming that a file opens.

    /// <summary>Unmasked deliberately - matching it against the document is the whole job.</summary>
    public string IdNumber { get; set; } = "";
    public bool IsSaIdNumber { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? MobileNumber { get; set; }

    public string? EmployerName { get; set; }
    public string? Occupation { get; set; }
    public decimal? GrossMonthlyIncome { get; set; }
    public decimal? NetMonthlyIncome { get; set; }

    /// <summary>Account holder name matters; the number does not. It stays masked.</summary>
    public string? BankAccountHolder { get; set; }
    public string? BankName { get; set; }
    public string? MaskedAccountNumber { get; set; }

    public long MaxFileSizeBytes { get; set; }
    public string AcceptedExtensions { get; set; } = "";

    public List<Row> Documents { get; set; } = new();

    /// <summary>Required document types with nothing usable on file yet.</summary>
    public List<DocumentType> Outstanding { get; set; } = new();

    public bool IsComplete => Outstanding.Count == 0;

    public string MaxFileSizeDisplay => $"{MaxFileSizeBytes / 1024d / 1024d:N0} MB";

    public class Row
    {
        public Guid Id { get; set; }
        public DocumentType DocumentType { get; set; }
        public string FileName { get; set; } = "";
        public long SizeBytes { get; set; }
        public DateTime UploadedUtc { get; set; }
        public string? UploadedBy { get; set; }

        public DocumentReviewStatus ReviewStatus { get; set; }
        public DateTime? ReviewedUtc { get; set; }
        public string? ReviewedBy { get; set; }
        public string? ReviewNotes { get; set; }

        public string SizeDisplay => SizeBytes < 1024 * 1024
            ? $"{SizeBytes / 1024d:N0} KB"
            : $"{SizeBytes / 1024d / 1024d:N1} MB";
    }
}
