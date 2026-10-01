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

/// <summary>
/// Where a client sits in the onboarding lifecycle.
/// <para>
/// Captured, Onboarded and Active are deliberately distinct. A captured client is data in
/// the system; an onboarded client has had that data verified against documents by someone
/// other than the person who captured it; an active client is one with money on the street.
/// Collapsing the three loses the ability to answer "how many real, lendable clients do we
/// have" - which is the number that actually matters.
/// </para>
/// <para>
/// The first four values keep their original numbers. Renumbering would silently restate
/// every client row already on file.
/// </para>
/// </summary>
public enum ClientStatus
{
    /// <summary>Capture started, wizard incomplete. Not a client yet.</summary>
    Draft = 0,

    /// <summary>Verified and currently holding at least one disbursed loan.</summary>
    Active = 1,

    /// <summary>Trading halted. No new credit, existing collections continue.</summary>
    Suspended = 2,

    /// <summary>File closed. No further activity.</summary>
    Closed = 3,

    /// <summary>Capture complete and consented, but nothing has been verified yet.</summary>
    Captured = 4,

    /// <summary>Documents submitted and sitting with a reviewer.</summary>
    PendingVerification = 5,

    /// <summary>Documents approved. Eligible to borrow, but not yet borrowing.</summary>
    Onboarded = 6,

    /// <summary>Verification failed. Retained for audit, cannot be lent to.</summary>
    Declined = 7
}

/// <summary>
/// Presentation for <see cref="ClientStatus"/>: the label an operator reads, the order the
/// lifecycle runs in, and the pipeline stage a status rolls up to.
/// </summary>
public static class ClientStatusInfo
{
    /// <summary>The lifecycle in the order it actually happens, for pipeline reporting.</summary>
    public static readonly IReadOnlyList<ClientStatus> Pipeline = new[]
    {
        ClientStatus.Draft,
        ClientStatus.Captured,
        ClientStatus.PendingVerification,
        ClientStatus.Onboarded,
        ClientStatus.Active
    };

    public static string Label(ClientStatus status) => status switch
    {
        ClientStatus.Draft => "Capture in progress",
        ClientStatus.Captured => "Captured",
        ClientStatus.PendingVerification => "Awaiting verification",
        ClientStatus.Onboarded => "Onboarded",
        ClientStatus.Active => "Active",
        ClientStatus.Suspended => "Suspended",
        ClientStatus.Closed => "Closed",
        ClientStatus.Declined => "Declined",
        _ => status.ToString()
    };

    /// <summary>One line explaining what the status means and what happens next.</summary>
    public static string Description(ClientStatus status) => status switch
    {
        ClientStatus.Draft => "Onboarding has been started but not completed.",
        ClientStatus.Captured => "All details captured. Supporting documents are still required.",
        ClientStatus.PendingVerification => "Documents uploaded and awaiting review.",
        ClientStatus.Onboarded => "Verified and eligible for credit. No loan raised yet.",
        ClientStatus.Active => "Holds at least one disbursed loan.",
        ClientStatus.Suspended => "Trading suspended. No new credit may be advanced.",
        ClientStatus.Closed => "File closed.",
        ClientStatus.Declined => "Verification was unsuccessful.",
        _ => string.Empty
    };

    /// <summary>True when the client may have a loan raised against them.</summary>
    public static bool CanBorrow(ClientStatus status) =>
        status is ClientStatus.Onboarded or ClientStatus.Active;
}

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

// ---------------------------------------------------------------------------------------
// Credit origination
// ---------------------------------------------------------------------------------------

/// <summary>
/// Credit package tier. Pricing and limits hang off this - see <c>CreditPackage</c>.
/// The tier itself is just a label; every number that matters is configured per tenant.
/// </summary>
public enum CreditTier
{
    Regular = 0,
    Gold = 1,
    Premium = 2
}

/// <summary>
/// Loan lifecycle. A loan is quoted and only becomes collectable once it is disbursed and
/// carries an authenticated DebiCheck mandate.
/// </summary>
public enum LoanStatus
{
    /// <summary>Quoted but not submitted. Numbers may still change.</summary>
    Draft = 0,
    /// <summary>Submitted for credit decision. Affordability has been run.</summary>
    PendingApproval = 1,
    Approved = 2,
    Declined = 3,
    /// <summary>Money out the door. Collections run against the mandate.</summary>
    Disbursed = 4,
    Settled = 5,
    Cancelled = 6,
    WrittenOff = 7
}

/// <summary>Outcome of an NCA s78-81 affordability assessment.</summary>
public enum AffordabilityOutcome
{
    /// <summary>Discretionary income comfortably covers the instalment.</summary>
    Pass = 0,
    /// <summary>Covered, but with little headroom. Requires a human decision.</summary>
    Marginal = 1,
    /// <summary>Instalment exceeds discretionary income. The NCA forbids the advance.</summary>
    Fail = 2,
    /// <summary>Not enough income or expense data captured to assess.</summary>
    Insufficient = 3
}

/// <summary>
/// Review state of an uploaded supporting document. A loan cannot be raised until the
/// client's mandatory documents are Approved.
/// </summary>
public enum DocumentReviewStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

/// <summary>
/// What a budget line is, for the NCA affordability test. Income is not a line kind: it is
/// captured once, on the Financial step, and never duplicated here.
/// </summary>
public enum BudgetLineKind
{
    /// <summary>A living expense. Tested against the Regulation 23A minimum as a whole.</summary>
    Expense = 1,

    /// <summary>
    /// An instalment to a credit provider. Deducted on top of living expenses, never in
    /// place of them: the Regulation 23A norm is a floor on living costs alone.
    /// </summary>
    DebtInstalment = 2
}
