using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AMPay.Domain.Netcash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AMPay.Infrastructure.Netcash;

/// <summary>
/// The real Pay Now hosted checkout. The customer's browser posts these fields to Netcash and
/// pays on Netcash's page, so no card data ever reaches AM-Pay (PCI DSS SAQ A).
/// <para>
/// Netcash's notify call carries no signature of its own. So every checkout sends a check value in
/// the third extra field (m6): an HMAC of the reference and amount under a secret only AM-Pay holds
/// (<c>Netcash:PayNowCallbackSecret</c>). Netcash echoes it back as Extra3, and a notify whose check
/// value does not match is refused: nobody can mark a payment paid without the secret.
/// </para>
/// <para>
/// Netcash sends the customer and the notify to the Accept, Decline and Notify URLs set on the Pay
/// Now service in the Netcash portal: /Checkout/Accepted, /Checkout/Declined and /Checkout/Notify.
/// </para>
/// </summary>
public class LiveNetcashPayNowService : INetcashPayNowService
{
    public const string SecretSetting = "Netcash:PayNowCallbackSecret";

    private readonly NetcashOptions _options;
    private readonly INetcashSecretStore _secrets;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LiveNetcashPayNowService> _log;

    public LiveNetcashPayNowService(
        IOptions<NetcashOptions> options, INetcashSecretStore secrets, IConfiguration configuration,
        ILogger<LiveNetcashPayNowService> log)
    {
        _options = options.Value;
        _secrets = secrets;
        _configuration = configuration;
        _log = log;
    }

    public async Task<NetcashResult<PayNowCheckoutForm>> BuildCheckoutAsync(
        string payNowServiceKey, PayNowCheckoutRequest request, CancellationToken ct = default)
    {
        // A placeholder key would send the customer to a broken Netcash page.
        if (!Guid.TryParse(payNowServiceKey, out _))
            return NetcashResult<PayNowCheckoutForm>.Fail(NetcashCodes.NoActiveServiceKey, "This business's Pay Now key is not loaded.");

        var secret = Secret();
        if (secret is null)
        {
            _log.LogError("Pay Now is live but {Setting} is not set, so payments could not be verified. Checkout refused.", SecretSetting);
            return NetcashResult<PayNowCheckoutForm>.Fail(NetcashCodes.ParameterError, "Pay Now is not fully set up on this server.");
        }

        var vendorKey = await _secrets.GetSoftwareVendorKeyAsync(ct);
        var amount = Amount(request.Amount);

        var fields = new Dictionary<string, string>
        {
            ["m1"] = payNowServiceKey,
            ["m2"] = vendorKey,
            ["p2"] = request.PaymentReference,
            ["p3"] = Limit(request.Description ?? "Payment", 50),
            ["p4"] = amount,
            ["m4"] = request.ExtraField1 ?? "",
            ["m5"] = request.ExtraField2 ?? "",
            ["m6"] = CheckValue(secret, request.PaymentReference, amount),
            ["m9"] = request.CustomerEmail ?? "",
            ["m11"] = request.CustomerMobile ?? ""
        };

        return NetcashResult<PayNowCheckoutForm>.Ok(new PayNowCheckoutForm { PostUrl = _options.PayNowCheckoutUrl, Fields = fields });
    }

    public Task<NetcashResult<PayNowCallback>> ParseCallbackAsync(
        string payNowServiceKey, IDictionary<string, string> form, CancellationToken ct = default)
    {
        var secret = Secret();
        if (secret is null)
            return Task.FromResult(NetcashResult<PayNowCallback>.Fail(NetcashCodes.ParameterError, $"{SecretSetting} is not set; callback not trusted."));

        return Task.FromResult(Parse(secret, form));
    }

    /// <summary>Reads a notify and checks its echoed check value. Static so it can be tested without configuration.</summary>
    public static NetcashResult<PayNowCallback> Parse(string secret, IDictionary<string, string> form)
    {
        var reference = Get(form, "Reference", "p2");
        var amountRaw = Get(form, "Amount", "p4");
        var check = Get(form, "Extra3", "m6");

        var callback = new PayNowCallback
        {
            PaymentReference = reference,
            TransactionAccepted = Get(form, "TransactionAccepted"),
            Reason = Get(form, "Reason"),
            NetcashTransactionId = Get(form, "RequestTrace", "TransactionId"),
            Method = Get(form, "Method"),
            Raw = new Dictionary<string, string>(form)
        };

        if (decimal.TryParse(amountRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            callback.Amount = amount;

        if (reference is null || callback.Amount is null || check is null ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(check), Encoding.ASCII.GetBytes(CheckValue(secret, reference, Amount(callback.Amount.Value)))))
        {
            return NetcashResult<PayNowCallback>.Fail(NetcashCodes.AuthenticationFailure,
                "The Pay Now callback's check value did not match - it was not sent for a checkout AM-Pay created.");
        }

        return NetcashResult<PayNowCallback>.Ok(callback);
    }

    /// <summary>HMAC-SHA256 of "reference|amount", hex, 32 characters (Netcash's extra fields are short).</summary>
    public static string CheckValue(string secret, string reference, string amount)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{reference}|{amount}")))[..32];
    }

    private string? Secret() => _configuration[SecretSetting] is { Length: >= 32 } s ? s : null;

    private static string Amount(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? Get(IDictionary<string, string> form, params string[] names)
    {
        foreach (var n in names)
        {
            var match = form.FirstOrDefault(kv => string.Equals(kv.Key, n, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value)) return match.Value.Trim();
        }
        return null;
    }
}
