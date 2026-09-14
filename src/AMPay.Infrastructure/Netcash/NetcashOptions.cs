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
    public string PartnerEndpoint { get; set; } = "https://ws.netcash.co.za/NIWS/niws_partner.svc";

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

    /// <summary>Base URL Netcash posts callbacks back to. Must be publicly reachable over HTTPS.</summary>
    public string? CallbackBaseUrl { get; set; }

    /// <summary>Secret-store name holding the AM-Pay ISV software vendor key.</summary>
    public string SoftwareVendorKeySecretName { get; set; } = "Netcash:SoftwareVendorKey";
}
