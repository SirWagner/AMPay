using AMPay.Domain.Enums;

namespace AMPay.Domain.Netcash;

// ---------------------------------------------------------------------------
// Request / response shapes. These mirror the Netcash field vocabulary so that
// the mapping from our domain happens once, here, and not in every caller.
// ---------------------------------------------------------------------------

/// <summary>Request for the real-time TT1 call, NIWS_NIF.DebiCheckAuthenticate.</summary>
public class DebiCheckAuthenticateRequest
{
    public string AccountReference { get; set; } = string.Empty;

    /// <summary>Format NCDCT000000001. Must be a real-time template or Netcash returns 325.</summary>
    public string MandateTemplateId { get; set; } = string.Empty;

    /// <summary>0 = passport or business registration, 1 = SA ID number.</summary>
    public bool IsIdNumber { get; set; } = true;
    public string DebtorIdentification { get; set; } = string.Empty;

    /// <summary>Masterfile account name.</summary>
    public string AccountName { get; set; } = string.Empty;

    public string BankAccountName { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string BankAccountNumber { get; set; } = string.Empty;
    public BankAccountType BankAccountType { get; set; } = BankAccountType.Cheque;

    public string MobileNumber { get; set; } = string.Empty;
    public string? EmailAddress { get; set; }

    public decimal CollectionAmount { get; set; }
    public bool FirstCollectionDiffers { get; set; }
    public decimal? FirstCollectionAmount { get; set; }
    public DateTime? FirstCollectionDate { get; set; }

    public string? CollectionDayCode { get; set; }
}

/// <summary>Response from a TT1 authentication attempt.</summary>
public class DebiCheckAuthenticateResponse
{
    public string? ContractReference { get; set; }
    public string? BankResponseCode { get; set; }
    public string? BankservResponseCode { get; set; }
    public string? ClientResponseCode { get; set; }

    /// <summary>Accepted or Rejected.</summary>
    public string? Status { get; set; }

    public bool IsAccepted =>
        string.Equals(Status, "Accepted", StringComparison.OrdinalIgnoreCase)
        && BankservResponseCode == NetcashCodes.BankservAccepted;
}

/// <summary>
/// The NetConnector postback Netcash sends when a bank answers a TT2 or delayed TT1 request.
/// Shape taken from the DebiCheck authentication documentation.
/// </summary>
public class DebiCheckPostback
{
    public string? AccountReference { get; set; }
    public string? ContractReference { get; set; }

    /// <summary>Initiation, Amendment or Cancellation.</summary>
    public string? Process { get; set; }

    /// <summary>Accepted or Rejected.</summary>
    public string? Status { get; set; }

    public bool? RMS { get; set; }
    public DateTime? Timestamp { get; set; }

    /// <summary>Discriminator, e.g. DEBICHECKRESULT.</summary>
    public string? Type { get; set; }
}

/// <summary>Request for the synchronous Netcash eMandate, NIWS_NIF.AddMandate.</summary>
public class AddMandateRequest
{
    /// <summary>2 to 22 characters. The reference used to address this mandate from now on.</summary>
    public string AccountReference { get; set; } = string.Empty;
    public string MandateName { get; set; } = string.Empty;
    public decimal MandateAmount { get; set; }

    /// <summary>False = business, true = individual.</summary>
    public bool IsConsumer { get; set; } = true;

    public string? FirstName { get; set; }
    public string? Surname { get; set; }
    public string? TradingName { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? RegisteredName { get; set; }

    /// <summary>Format 0825551234.</summary>
    public string MobileNumber { get; set; } = string.Empty;
    public string? EmailAddress { get; set; }

    public DebitFrequency DebitFrequency { get; set; } = DebitFrequency.Monthly;

    /// <summary>MM.</summary>
    public int CommencementMonth { get; set; }

    /// <summary>01 to 31, or LDOM for last day of month.</summary>
    public string CommencementDay { get; set; } = "01";

    public DateTime AgreementDate { get; set; } = DateTime.UtcNow.Date;

    public BankingDetailType BankDetailType { get; set; } = BankingDetailType.BankAccount;
    public string? BankAccountNumber { get; set; }
    public string? BranchCode { get; set; }
}

/// <summary>AddMandate returns a hosted URL where the debtor signs, not a completed mandate.</summary>
public class AddMandateResponse
{
    public string? MandateUrl { get; set; }
    public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}

/// <summary>Real-time account verification.</summary>
public class AvsRequest
{
    public string AccountNumber { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public BankAccountType AccountType { get; set; } = BankAccountType.Cheque;
    public string? IdNumber { get; set; }
    public string? Initials { get; set; }
    public string? Surname { get; set; }
    public string? CompanyRegistrationNumber { get; set; }
}

/// <summary>
/// AVS answers a set of independent questions, each of which can be yes, no, or not
/// answered by the bank. Nullable bools carry that third state honestly.
/// </summary>
public class AvsResponse
{
    public bool? AccountExists { get; set; }
    public bool? AccountOpenAtLeastThreeMonths { get; set; }
    public bool? IdNumberMatches { get; set; }
    public bool? InitialsMatch { get; set; }
    public bool? SurnameMatches { get; set; }
    public bool? AccountAcceptsDebits { get; set; }
    public bool? AccountAcceptsCredits { get; set; }
    public string? Reference { get; set; }
}

/// <summary>Result of ValidateServiceKey on the NIWS_Partner endpoint.</summary>
public class ServiceKeyValidation
{
    public NetcashServiceId ServiceId { get; set; }
    public ServiceKeyStatus Status { get; set; }
    public string? Message { get; set; }
}

/// <summary>A Pay Now checkout, resolved to the form fields the hosted page expects.</summary>
public class PayNowCheckoutRequest
{
    public string PaymentReference { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerMobile { get; set; }

    public string? ExtraField1 { get; set; }
    public string? ExtraField2 { get; set; }
    public string? ExtraField3 { get; set; }
}

/// <summary>
/// Everything needed to POST the customer across to Netcash: the target URL and the exact
/// form fields. Returned rather than posted directly so the redirect stays in the browser,
/// which is what keeps card data out of this application.
/// </summary>
public class PayNowCheckoutForm
{
    public string PostUrl { get; set; } = string.Empty;
    public IDictionary<string, string> Fields { get; set; } = new Dictionary<string, string>();
}

/// <summary>The accept, decline and notify callback Netcash sends back from Pay Now.</summary>
public class PayNowCallback
{
    public string? PaymentReference { get; set; }
    public string? TransactionAccepted { get; set; }
    public string? Reason { get; set; }
    public decimal? Amount { get; set; }
    public string? NetcashTransactionId { get; set; }
    public string? Method { get; set; }
    public IDictionary<string, string> Raw { get; set; } = new Dictionary<string, string>();

    public bool IsAccepted =>
        string.Equals(TransactionAccepted, "true", StringComparison.OrdinalIgnoreCase);
}
