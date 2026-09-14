namespace AMPay.Domain.Enums;

/// <summary>Netcash service identifiers. Each maps to a separate service key on the merchant account.</summary>
public enum NetcashServiceId
{
    DebitOrders = 1,
    CreditorPayments = 2,
    RiskReports = 3,
    Account = 5,
    SalaryPayments = 10,
    PayNow = 14
}

/// <summary>Result of ValidateServiceKey against the NIWS_Partner endpoint.</summary>
public enum ServiceKeyStatus
{
    Unverified = 0,
    Validated = 1,
    NoActiveService = 105,
    NoActiveServiceKey = 106,
    AuthenticationFailed = 100,
    AccountLockedOut = 201,
    ServiceError = 200
}

public enum TenantStatus { Onboarding = 0, Active = 1, Suspended = 2, Closed = 3 }

public enum ClientStatus { Draft = 0, Active = 1, Suspended = 2, Closed = 3 }

/// <summary>Netcash field 133 / BankAccountType. Netcash accepts 1 and 2 only.</summary>
public enum BankAccountType { Cheque = 1, Savings = 2 }

/// <summary>Netcash field 131 / BankDetailType.</summary>
public enum BankingDetailType { BankAccount = 1, CreditCard = 2 }

/// <summary>Mirrors the Netcash AddMandate DebitFrequency enum exactly.</summary>
public enum DebitFrequency
{
    Monthly = 0,
    Bimonthly = 1,
    ThreeMonthly = 2,
    SixMonthly = 3,
    Annually = 4,
    Weekly = 5,
    Biweekly = 6
}

/// <summary>
/// How a mandate is authenticated. TT1 answers in-session; TT2 answers later via NetConnector postback.
/// </summary>
public enum MandateType
{
    /// <summary>Real-time DebiCheck via DebiCheckAuthenticate. Requires a real-time mandate template.</summary>
    DebiCheckTt1RealTime = 1,
    /// <summary>Batch DebiCheck via BatchFileUpload. Requires the masterfile entry to exist first.</summary>
    DebiCheckTt2Batch = 2,
    /// <summary>Delayed TT1 - submitted in batch, authenticated by the debtor out of band.</summary>
    DebiCheckDelayedTt1 = 3,
    /// <summary>Netcash electronic mandate (AddMandate) - debtor signs on a hosted Netcash page.</summary>
    NetcashEMandate = 4
}

/// <summary>
/// Mandate lifecycle. TT1 can reach Authenticated synchronously; TT2 passes through
/// SubmittedToBank and waits for the DEBICHECKRESULT postback.
/// </summary>
public enum MandateStatus
{
    Draft = 0,
    PendingMasterfile = 1,
    SubmittedToBank = 2,
    AwaitingDebtorAuthentication = 3,
    Authenticated = 4,
    Rejected = 5,
    Cancelled = 6,
    Amended = 7,
    Failed = 8,
    Expired = 9
}

public enum PaymentStatus { Created = 0, Redirected = 1, Complete = 2, Declined = 3, Cancelled = 4, Expired = 5 }

public enum AddressType { Physical = 0, Postal = 1, Work = 2 }

public enum DocumentType
{
    IdDocument = 0,
    Payslip = 1,
    ProofOfBankAccount = 2,
    ProofOfAddress = 3,
    Combined = 4,
    SignedMandate = 5,
    Other = 99
}

public enum BatchStatus { Draft = 0, Uploaded = 1, ReportRequested = 2, Successful = 3, SuccessfulWithErrors = 4, Unsuccessful = 5 }
