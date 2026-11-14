using System.ComponentModel.DataAnnotations;
using AMPay.Domain.Credit;
using AMPay.Domain.Entities;

namespace AMPay.Web.Models;

/// <summary>The Good to go step: the flag, what it was built from, and the checks staff run from here.</summary>
public class GoodToGoPageModel
{
    public Client Client { get; set; } = null!;
    public ClientScreening Screening { get; set; } = null!;
    public IReadOnlyList<GoodToGoCheck> Checks { get; set; } = Array.Empty<GoodToGoCheck>();
    public List<ClientScreening> History { get; set; } = new();

    public ClientVerification? Life { get; set; }
    public ClientVerification? Employment { get; set; }

    /// <summary>The number to phone, from the employer list when the employer was picked from it.</summary>
    public string? EmployerTelephone { get; set; }
    public string? EmployerContact { get; set; }

    public List<ClientDocument> Statements { get; set; } = new();
    public Dictionary<Guid, BankStatementAssessment> Assessments { get; set; } = new();

    /// <summary>False while the stub answers: the page then says the assessment is simulated.</summary>
    public bool AnalystLive { get; set; }
    public bool AffordabilityCaptured { get; set; }
    public bool CanOverride { get; set; }
    public int RecheckDays { get; set; }

    public static readonly string[] EmploymentMethods =
        { "Phoned the employer", "Payslip checked", "Letter from the employer", "Email from HR" };
}

/// <summary>The Notes step: the client's contact history.</summary>
public class ClientNotesModel
{
    public Guid ClientId { get; set; }
    public List<ClientNote> Notes { get; set; } = new();
}

public class FollowUpRow
{
    public Guid NoteId { get; set; }
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string ClientNumber { get; set; } = "";
    public string? Mobile { get; set; }
    public string? Channel { get; set; }
    public string Body { get; set; } = "";
    public DateTime FollowUpOn { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>The outcome report handed to the client: what was decided, and why, in their words.</summary>
public class ClientReportModel
{
    public Client Client { get; set; } = null!;
    public string Lender { get; set; } = "";
    public string? LenderNcr { get; set; }
    public string? LenderContact { get; set; }
    public ClientScreening Screening { get; set; } = null!;
    public List<string> Reasons { get; set; } = new();
    public List<string> StillNeeded { get; set; } = new();
    public NetOfNetView? NetOfNet { get; set; }

    /// <summary>The bureau whose report was used, when a credit check was done (NCA s62: the client may ask).</summary>
    public string? Bureau { get; set; }
    public DateTime? BureauCheckedUtc { get; set; }

    /// <summary>
    /// A check, said the way the client should hear it. Null for checks that are the lender's own
    /// business (warnings, internal follow-ups) and do not belong on a letter to the client.
    /// </summary>
    public static string? Wording(GoodToGoCheck check) => (check.Name, check.Status) switch
    {
        ("Identity", CheckStatus.Fail) => "The ID number we have for you is not a valid South African ID number.",
        ("Age", CheckStatus.Fail) => "You must be 18 or older to enter a credit agreement.",
        ("Home Affairs", CheckStatus.Fail) => "We could not confirm your identity with Home Affairs.",
        ("Employment", CheckStatus.Fail) => "You are not employed at the moment, so there is no regular income to repay a loan from.",
        ("Contract", CheckStatus.Fail) => "Your employment contract has ended.",
        ("Employment verified", CheckStatus.Fail) => "Your employer did not confirm your employment.",
        ("Affordability", CheckStatus.Fail) => "After your living expenses and existing debt there is not enough left each month to repay a loan. The National Credit Act does not allow us to lend in that case.",
        ("Bank statement", CheckStatus.Fail) => "Your bank statement shows difficulty keeping up with what you already owe.",
        ("History", CheckStatus.Fail) => "You have an earlier loan with us that is in arrears or was written off.",

        ("Home Affairs", CheckStatus.Missing) => "Confirmation of your identity with Home Affairs.",
        ("POPIA consent", CheckStatus.Missing) => "Your consent for us to process your personal information.",
        ("Mobile number", CheckStatus.Missing) => "A cellphone number we can reach you on.",
        ("Employment", CheckStatus.Missing) => "Your employment details.",
        ("Employment verified", CheckStatus.Missing) => "Confirmation of your employment from your employer.",
        ("Income", CheckStatus.Missing) => "Your monthly income.",
        ("Budget", CheckStatus.Missing) => "Your monthly living expenses.",
        ("Bank account", CheckStatus.Missing) => "Your bank account details.",
        ("Age", CheckStatus.Missing) => "Your date of birth.",
        _ => null
    };
}

public class EmployerEditModel
{
    public Guid? Id { get; set; }

    [Required, StringLength(200), Display(Name = "Employer name")]
    public string Name { get; set; } = "";

    [StringLength(100), Display(Name = "Pay point")]
    public string? PayPoint { get; set; }

    [StringLength(20), Display(Name = "HR / payroll telephone")]
    public string? Telephone { get; set; }

    [StringLength(200), Display(Name = "Contact person")]
    public string? ContactPerson { get; set; }

    [StringLength(200), EmailAddress, Display(Name = "Email")]
    public string? EmailAddress { get; set; }

    [StringLength(300)]
    public string? Address { get; set; }

    [Display(Name = "Pay frequency")]
    public string? PayFrequency { get; set; }

    [Range(1, 31), Display(Name = "Salary day of month")]
    public int? SalaryDay { get; set; }

    [Display(Name = "We have confirmed this employer exists and the number is right")]
    public bool IsVerified { get; set; }

    [Display(Name = "Show in the employer list")]
    public bool IsActive { get; set; } = true;
}

public class EmployerRow
{
    public Employer Employer { get; set; } = null!;
    public int Clients { get; set; }
}

/// <summary>How the flag and its checks are coloured and named on every page that shows them.</summary>
public static class ScreeningDisplay
{
    public static string Tone(ScreeningOutcome outcome) => outcome switch
    {
        ScreeningOutcome.GoodToGo => "badge-green",
        ScreeningOutcome.Review => "badge-amber",
        _ => "badge-red"
    };

    public static string Tone(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "badge-green",
        CheckStatus.Warn => "badge-amber",
        CheckStatus.Missing => "badge-grey",
        _ => "badge-red"
    };

    public static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "OK",
        CheckStatus.Warn => "Check",
        CheckStatus.Missing => "Missing",
        _ => "Fail"
    };

    public static string Tone(VerificationResult result) => result switch
    {
        VerificationResult.Confirmed => "badge-green",
        VerificationResult.Failed => "badge-red",
        _ => "badge-amber"
    };

    public static string Tone(StatementFlag flag) => flag switch
    {
        StatementFlag.Good => "badge-green",
        StatementFlag.Warning => "badge-amber",
        _ => "badge-red"
    };
}
