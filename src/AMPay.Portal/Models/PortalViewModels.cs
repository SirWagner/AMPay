using System.ComponentModel.DataAnnotations;
using AMPay.Domain.Enums;
using AMPay.Portal.Data;
using AMPay.Portal.Services;

namespace AMPay.Portal.Models;

public class LandingModel
{
    public required Lender Lender { get; init; }
    public bool SignedIn { get; init; }
    public EstimateService.Limits? Range { get; init; }
}

public class StartModel
{
    [Required(ErrorMessage = "Enter your cell number.")]
    [Display(Name = "Cell number")]
    public string Mobile { get; set; } = "";
}

public class VerifyModel
{
    public Guid ChallengeId { get; set; }
    public string MaskedMobile { get; set; } = "";

    [Required(ErrorMessage = "Enter the 6-digit code.")]
    [RegularExpression(@"^\s*\d{6}\s*$", ErrorMessage = "The code is 6 digits.")]
    [Display(Name = "Code")]
    public string Code { get; set; } = "";

    public string? TestCode { get; set; }
}

public class AdvisorModel
{
    [Required(ErrorMessage = "Tell us your name."), StringLength(150)]
    [Display(Name = "Your name")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Enter the number to call you on.")]
    [Display(Name = "Cell number")]
    public string Mobile { get; set; } = "";

    [StringLength(60)]
    [Display(Name = "Best time to call")]
    public string? PreferredTime { get; set; }

    [StringLength(1000)]
    [Display(Name = "What would you like to discuss? (optional)")]
    public string? Message { get; set; }

    /// <summary>Left empty by people; filled by form-spam bots.</summary>
    public string? Website { get; set; }
}

public class DetailsModel
{
    [StringLength(30)] public string? Title { get; set; }

    [Required(ErrorMessage = "Enter your first name."), StringLength(150), Display(Name = "First name")]
    public string FirstName { get; set; } = "";

    [StringLength(150), Display(Name = "Middle names")]
    public string? MiddleNames { get; set; }

    [Required(ErrorMessage = "Enter your surname."), StringLength(150)]
    public string Surname { get; set; } = "";

    [Required(ErrorMessage = "Enter your SA ID number."), Display(Name = "SA ID number")]
    public string IdNumber { get; set; } = "";

    [EmailAddress(ErrorMessage = "That is not a valid email address."), StringLength(254), Display(Name = "Email (optional)")]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Enter your street address."), StringLength(150), Display(Name = "Street address")]
    public string AddressLine1 { get; set; } = "";

    [StringLength(150), Display(Name = "Address line 2")]
    public string? AddressLine2 { get; set; }

    [StringLength(150)] public string? Suburb { get; set; }

    [Required(ErrorMessage = "Enter your town or city."), StringLength(150), Display(Name = "Town or city")]
    public string City { get; set; } = "";

    [StringLength(150)] public string? Province { get; set; }

    [StringLength(10), Display(Name = "Postal code")]
    public string? PostalCode { get; set; }
}

public class IncomeModel
{
    [Required(ErrorMessage = "Enter your employer."), StringLength(150), Display(Name = "Employer")]
    public string EmployerName { get; set; } = "";

    [StringLength(150), Display(Name = "Job title")]
    public string? Occupation { get; set; }

    [DataType(DataType.Date), Display(Name = "Working there since")]
    public DateTime? EmployedSince { get; set; }

    [Display(Name = "Paid")]
    public string? PayFrequency { get; set; } = "Monthly";

    [Range(1, 31, ErrorMessage = "Pay day is a day of the month, 1 to 31."), Display(Name = "Pay day (day of the month)")]
    public int? SalaryDay { get; set; }

    [Required(ErrorMessage = "Enter your gross monthly salary."), Range(0, 10_000_000), Display(Name = "Gross salary (before deductions)")]
    public decimal? GrossMonthlyIncome { get; set; }

    [Required(ErrorMessage = "Enter your take-home pay."), Range(0, 10_000_000), Display(Name = "Take-home pay (after deductions)")]
    public decimal? NetMonthlyIncome { get; set; }

    [Range(0, 10_000_000), Display(Name = "Other monthly income")]
    public decimal? OtherMonthlyIncome { get; set; }

    [Required(ErrorMessage = "Estimate your monthly living expenses."), Range(0, 10_000_000), Display(Name = "Monthly living expenses")]
    public decimal? MonthlyExpenses { get; set; }

    [Range(0, 10_000_000), Display(Name = "Existing loan and account repayments")]
    public decimal? MonthlyDebtRepayments { get; set; }
}

public class LoanModel
{
    [Required(ErrorMessage = "Enter the amount you need."), Display(Name = "Amount you need (R)")]
    public decimal? Amount { get; set; }

    [Required(ErrorMessage = "Choose how many months to repay over."), Display(Name = "Repay over (months)")]
    public int? Term { get; set; }

    public EstimateService.Limits? Range { get; set; }
    public EstimateService.Estimate? Estimate { get; set; }
}

public class DocumentsModel
{
    public required LoanApplication Application { get; init; }
    public long MaxFileSizeBytes { get; init; }

    public static readonly (DocumentType Type, string Label, string Hint, bool Required)[] Kinds =
    {
        (DocumentType.IdDocument, "ID document", "Your green ID book or both sides of your smart ID card.", true),
        (DocumentType.Payslip, "Latest payslip", "Your most recent payslip.", true),
        (DocumentType.ProofOfBankAccount, "Bank statement", "Your latest 3 months' statement, showing your salary going in.", false)
    };
}

public class ReviewModel
{
    public required LoanApplication Application { get; init; }
    public required IReadOnlyList<string> Missing { get; init; }

    [Display(Name = "POPIA consent")]
    public bool DataProcessingConsent { get; set; }

    [Display(Name = "Credit check consent")]
    public bool CreditCheckConsent { get; set; }

    public bool MarketingConsent { get; set; }
}
