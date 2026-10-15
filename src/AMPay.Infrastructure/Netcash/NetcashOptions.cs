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

    public bool Any => Partner;
}
