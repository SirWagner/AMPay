using AMPay.Domain.Enums;

namespace AMPay.Domain.Netcash;

/// <summary>
/// Resolves a tenant Netcash service key from the secret store.
/// <para>
/// Service keys are live payment credentials. They are never stored in the application
/// database and never written to logs; the database holds only a secret name that this
/// service resolves at the moment of use.
/// </para>
/// </summary>
public interface INetcashSecretStore
{
    /// <summary>Resolve a service key by its secret name. Returns null when not configured.</summary>
    Task<string?> GetServiceKeyAsync(string secretName, CancellationToken ct = default);

    /// <summary>The ISV software vendor key issued to AM-Pay by Netcash.</summary>
    Task<string> GetSoftwareVendorKeyAsync(CancellationToken ct = default);
}

/// <summary>
/// NIWS_Partner endpoint. This is the ISV surface: AM-Pay validates that a customer
/// account and its service keys are live before acting on that customer behalf.
/// </summary>
public interface INetcashPartnerService
{
    /// <summary>
    /// ValidateServiceKey. Netcash requires this at least every 24 hours and on each login.
    /// Three failures inside ten minutes lock the merchant account, so callers must not retry
    /// blindly on a failure response.
    /// </summary>
    Task<NetcashResult<IReadOnlyList<ServiceKeyValidation>>> ValidateServiceKeysAsync(
        string merchantAccountNumber,
        IReadOnlyDictionary<NetcashServiceId, string> serviceKeys,
        CancellationToken ct = default);
}

/// <summary>NIWS_NIF endpoint, DebiCheck surface.</summary>
public interface INetcashDebiCheckService
{
    /// <summary>
    /// TT1 real-time authentication. Synchronous: the bank answer arrives inside this call.
    /// Netcash requires a client timeout of at least three minutes.
    /// </summary>
    Task<NetcashResult<DebiCheckAuthenticateResponse>> AuthenticateRealTimeAsync(
        string debitOrderServiceKey,
        DebiCheckAuthenticateRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// TT2 batch authentication via BatchFileUpload with instruction DebiCheckAuthentication.
    /// Returns a file token; the outcome arrives later on the NetConnector postback.
    /// <para>
    /// The debit order masterfile entry must already exist at Netcash, so callers must send an
    /// Update batch first. See <see cref="INetcashDebitOrderService.UploadMasterfileAsync"/>.
    /// </para>
    /// </summary>
    Task<NetcashResult<string>> AuthenticateBatchAsync(
        string debitOrderServiceKey,
        IReadOnlyList<DebiCheckAuthenticateRequest> requests,
        DateTime actionDate,
        string batchName,
        CancellationToken ct = default);

    /// <summary>DebiCheckAuthenticationCurrentStatus - poll a single mandate.</summary>
    Task<NetcashResult<string>> GetAuthenticationStatusAsync(
        string debitOrderServiceKey,
        string contractReference,
        CancellationToken ct = default);

    /// <summary>DebiCheckAmendAuthentication - change terms without re-authenticating.</summary>
    Task<NetcashResult<bool>> AmendAuthenticationAsync(
        string debitOrderServiceKey,
        string contractReference,
        decimal newAmount,
        CancellationToken ct = default);

    /// <summary>DebiCheckCancelAuthentication - revoke a mandate, with a reason code.</summary>
    Task<NetcashResult<bool>> CancelAuthenticationAsync(
        string debitOrderServiceKey,
        string contractReference,
        string reasonCode,
        CancellationToken ct = default);

    /// <summary>DebiCheckRetrieveMandateTemplate - list templates configured for the account.</summary>
    Task<NetcashResult<IReadOnlyList<string>>> RetrieveMandateTemplatesAsync(
        string debitOrderServiceKey,
        CancellationToken ct = default);
}

/// <summary>NIWS_NIF endpoint, debit order and eMandate surface.</summary>
public interface INetcashDebitOrderService
{
    /// <summary>
    /// BatchFileUpload with instruction Update. Creates or updates masterfile entries.
    /// Required before a TT2 DebiCheck authentication will be accepted.
    /// </summary>
    Task<NetcashResult<string>> UploadMasterfileAsync(
        string debitOrderServiceKey,
        string nifFileContent,
        CancellationToken ct = default);

    /// <summary>BatchFileUpload with instruction Sameday or TwoDay. Returns a file token.</summary>
    Task<NetcashResult<string>> UploadCollectionBatchAsync(
        string debitOrderServiceKey,
        string nifFileContent,
        CancellationToken ct = default);

    /// <summary>RequestFileUploadReport. The only place per-line validation errors appear.</summary>
    Task<NetcashResult<string>> GetLoadReportAsync(
        string debitOrderServiceKey,
        string fileToken,
        CancellationToken ct = default);

    /// <summary>AddMandate - synchronous eMandate, returns a hosted URL for the debtor to sign.</summary>
    Task<NetcashResult<AddMandateResponse>> AddMandateAsync(
        string debitOrderServiceKey,
        AddMandateRequest request,
        CancellationToken ct = default);

    /// <summary>RequestActionDate - confirm a date is a valid banking action date before submitting.</summary>
    Task<NetcashResult<DateTime>> RequestActionDateAsync(
        string debitOrderServiceKey,
        DateTime desiredDate,
        CancellationToken ct = default);
}

/// <summary>NIWS_Validation endpoint. Cheap pre-flight checks that avoid batch rejections.</summary>
public interface INetcashValidationService
{
    /// <summary>AVSRealtimeQuery - live account verification.</summary>
    Task<NetcashResult<AvsResponse>> VerifyAccountAsync(
        string serviceKey, AvsRequest request, CancellationToken ct = default);

    /// <summary>ValidateBankAccount - format and CDV check. Does not prove the account exists.</summary>
    Task<NetcashResult<bool>> ValidateBankAccountAsync(
        string accountNumber, string branchCode, BankAccountType accountType,
        CancellationToken ct = default);

    /// <summary>ValidateId - SA identity number check.</summary>
    Task<NetcashResult<bool>> ValidateIdNumberAsync(string idNumber, CancellationToken ct = default);

    /// <summary>GetBankListWithDefaultBranchCode.</summary>
    Task<NetcashResult<IReadOnlyList<BankInfo>>> GetBankListAsync(CancellationToken ct = default);

    /// <summary>GetDebiCheckParticipatingBanks - not every bank processes DebiCheck.</summary>
    Task<NetcashResult<IReadOnlyList<BankInfo>>> GetDebiCheckParticipatingBanksAsync(
        CancellationToken ct = default);
}

public class BankInfo
{
    public string Name { get; set; } = string.Empty;
    public string? DefaultBranchCode { get; set; }
    public bool SupportsDebiCheck { get; set; }
}

/// <summary>PayNow.svc plus the hosted checkout form.</summary>
public interface INetcashPayNowService
{
    /// <summary>
    /// Build the form that posts the customer to the Netcash hosted payment page.
    /// Returns the fields rather than performing the post, so card entry stays on
    /// Netcash infrastructure and this application remains PCI DSS SAQ A.
    /// </summary>
    Task<NetcashResult<PayNowCheckoutForm>> BuildCheckoutAsync(
        string payNowServiceKey,
        PayNowCheckoutRequest request,
        CancellationToken ct = default);

    /// <summary>Parse and verify an accept, decline or notify callback.</summary>
    Task<NetcashResult<PayNowCallback>> ParseCallbackAsync(
        string payNowServiceKey,
        IDictionary<string, string> form,
        CancellationToken ct = default);
}

/// <summary>
/// Credit bureau enquiry.
/// <para>
/// GAP - deliberately not implemented. Compuscan / Experian is a separate vendor contract
/// outside the Netcash scope. Netcash does expose RequestCreditDataReport (risk reports,
/// service id 3), which may satisfy this instead; decide before wiring an implementation.
/// </para>
/// </summary>
public interface ICreditBureauClient
{
    Task<NetcashResult<string>> RequestReportAsync(
        string idNumber, string reportType, CancellationToken ct = default);
}
