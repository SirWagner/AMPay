using AMPay.Domain.Enums;

namespace AMPay.Infrastructure.Netcash;

/// <summary>
/// Netcash endpoint and ISV configuration, bound from the Netcash configuration section.
/// <para>
/// Secrets do not belong here. The software vendor key and per-tenant service keys are
/// resolved through INetcashSecretStore so they can come from user-secrets in development
/// and Azure Key Vault in production without a code change.
/// </para>
/// </summary>
public class NetcashOptions
{
    public const string SectionName = "Netcash";

    /// <summary>Core services: DebiCheck, debit orders, batch upload, mandates.</summary>
    public string NifEndpoint { get; set; } = "https://ws.netcash.co.za/NIWS/NIWS_NIF.svc";

    /// <summary>Bank, branch, ID and account validation.</summary>
    public string ValidationEndpoint { get; set; } = "https://ws.netcash.co.za/NIWS/NIWS_Validation.svc";

    /// <summary>ISV surface: ValidateServiceKey, account status. SOAP 1.2.</summary>
    public string PartnerEndpoint { get; set; } = "https://ws.netcash.co.za/NIWS/NIWS_Partner.svc";

    /// <summary>Pay Now service operations.</summary>
    public string PayNowEndpoint { get; set; } = "https://ws.netcash.co.za/PayNow/PayNow.svc";

    /// <summary>Where the customer browser is posted to start a Pay Now checkout.</summary>
    public string PayNowCheckoutUrl { get; set; } = "https://paynow.netcash.co.za/site/paynow.aspx";

    /// <summary>
    /// Netcash requires a client timeout of at least three minutes on the synchronous TT1 call,
    /// because it waits on the debtor bank.
    /// </summary>
    public int RealTimeTimeoutSeconds { get; set; } = 200;

    /// <summary>Timeout for everything else.</summary>
    public int DefaultTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// When true, no live Netcash call is made and the stub clients answer instead.
    /// Keep this true until the sandbox service keys are loaded.
    /// </summary>
    public bool UseStubs { get; set; } = true;

    /// <summary>
    /// Services switched to the real Netcash one at a time while <see cref="UseStubs"/> stays
    /// true for the rest - so a test account can be proved service by service.
    /// </summary>
    public NetcashLiveServices Live { get; set; } = new();

    /// <summary>
    /// Which of the business's keys the validation service (bank account, ID, bank lists) is
    /// called with. Netcash ties it to the Account service.
    /// </summary>
    public NetcashServiceId ValidationKeyService { get; set; } = NetcashServiceId.Account;

    /// <summary>Which key AVS (on the NIF debit-order endpoint) is called with.</summary>
    public NetcashServiceId AvsKeyService { get; set; } = NetcashServiceId.DebitOrders;

    /// <summary>Base URL Netcash posts callbacks back to. Must be publicly reachable over HTTPS.</summary>
    public string? CallbackBaseUrl { get; set; }

    /// <summary>Secret-store name holding the AM-Pay ISV software vendor key.</summary>
    public string SoftwareVendorKeySecretName { get; set; } = "Netcash:SoftwareVendorKey";
}

/// <summary>Per-service live switches (configuration <c>Netcash:Live:*</c>). All off by default.</summary>
public class NetcashLiveServices
{
    /// <summary>NIWS_Partner: ValidateServiceKey. Read-only - the safe first live call.</summary>
    public bool Partner { get; set; }

    /// <summary>NIWS_Validation: bank account format, ID number, bank lists. Read-only.</summary>
    public bool Validation { get; set; }

    /// <summary>NIWS_NIF.AVSRealtimeQuery: asks the bank whether an account exists and matches. Read-only, but each query may be billed.</summary>
    public bool Avs { get; set; }

    /// <summary>
    /// NIWS_NIF DebiCheck: real-time (TT1) authentication, status and cancel. NOT read-only: TT1
    /// asks the debtor's bank to prompt the account holder, so use accounts whose holder expects it.
    /// </summary>
    public bool DebiCheck { get; set; }

    /// <summary>Pay Now hosted checkout and its notify. Real money on a live account; needs Netcash:PayNowCallbackSecret.</summary>
    public bool PayNow { get; set; }

    /// <summary>Risk reports: credit bureau checks (TransUnion, Experian, XDS). Billed, and footprinted on the client's record.</summary>
    public bool Risk { get; set; }

    public bool Any => Partner || Validation || Avs || DebiCheck || PayNow || Risk;

    /// <summary>Names of the live services, for the banner.</summary>
    public IEnumerable<string> Names()
    {
        if (Partner) yield return "service-key validation";
        if (Validation) yield return "bank and ID checks";
        if (Avs) yield return "bank account verification (AVS)";
        if (DebiCheck) yield return "DebiCheck real-time mandates (TT1)";
        if (PayNow) yield return "Pay Now payments";
        if (Risk) yield return "credit bureau checks";
    }
}
