using System.Globalization;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AMPay.Infrastructure.Netcash;

/// <summary>
/// The real DebiCheck calls on NIWS_NIF (SOAP 1.1): real-time TT1 authentication, the status of
/// a mandate, cancelling one, and confirming the account's mandate templates.
/// <para>
/// TT1 sends an authentication request to the debtor's bank, which asks the account holder to
/// approve it in their banking app; the bank's answer comes back inside this call, so it waits
/// up to <c>RealTimeTimeoutSeconds</c>. Use only bank accounts whose holder expects the request.
/// </para>
/// <para>
/// Batch (TT2) and amendments are not connected yet and say so, rather than answering from the
/// stub: a mandate the app believes was sent, but Netcash never received, is worse than an error.
/// </para>
/// </summary>
public class LiveNetcashDebiCheckService : INetcashDebiCheckService
{
    private const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Tempuri = "http://tempuri.org/";
    private const string Niws = "http://schemas.datacontract.org/2004/07/NC.DG.TMS.C.WCF.NIWS";
    private const string ArraysNs = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";

    /// <summary>Netcash's cancellation reasons. "01" from older screens means the customer asked.</summary>
    public static readonly string[] CancellationReasons = { "AGNT", "CURR", "CUST", "CUTA", "DUPL", "FRAD", "UPAY", "FRTR" };

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) });

    private readonly NetcashOptions _options;
    private readonly INetcashKeyProvider _keys;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LiveNetcashDebiCheckService> _log;

    public LiveNetcashDebiCheckService(
        IOptions<NetcashOptions> options, INetcashKeyProvider keys, IConfiguration configuration,
        ILogger<LiveNetcashDebiCheckService> log)
    {
        _options = options.Value;
        _keys = keys;
        _configuration = configuration;
        _log = log;
    }

    // ---------------------------------------------------------------- TT1

    public async Task<NetcashResult<DebiCheckAuthenticateResponse>> AuthenticateRealTimeAsync(
        string debitOrderServiceKey, DebiCheckAuthenticateRequest request, CancellationToken ct = default)
    {
        var key = await KeyAsync(debitOrderServiceKey, ct);
        if (key is null) return MissingKey<DebiCheckAuthenticateResponse>();

        if (string.IsNullOrWhiteSpace(request.MandateTemplateId))
            return NetcashResult<DebiCheckAuthenticateResponse>.Fail(NetcashCodes.ParameterError,
                "Choose the DebiCheck mandate template set up on your Netcash account.");

        var body = BuildAuthenticate(key, request);
        var (xml, error) = await PostAsync("DebiCheckAuthenticate", body, _options.RealTimeTimeoutSeconds, ct);
        if (error is not null) return NetcashResult<DebiCheckAuthenticateResponse>.Fail(NetcashCodes.GeneralServiceError, error);

        var result = ParseAuthenticate(xml!);
        _log.LogInformation("Netcash DebiCheck TT1 for {Reference}: {Outcome}.", request.AccountReference,
            result.Success
                ? $"status {result.Data!.Status}, bank {result.Data.BankResponseCode}, Bankserv {result.Data.BankservResponseCode}"
                : $"{result.Code} {result.Message}");
        return result;
    }

    public static string BuildAuthenticate(string key, DebiCheckAuthenticateRequest r)
    {
        var dayCode = DayCode(r.CollectionDayCode, r.FirstCollectionDate);
        return
            $"<DebiCheckAuthenticate xmlns=\"{Tempuri}\">" +
            El("ServiceKey", key) +
            El("AccountReference", r.AccountReference) +
            El("DebiCheckMandateTemplateId", r.MandateTemplateId.Trim()) +
            El("IsIdNumber", r.IsIdNumber ? "true" : "false") +
            El("DebtorIdentification", r.DebtorIdentification) +
            El("AccountName", r.AccountName) +
            El("BankAccountName", r.BankAccountName) +
            El("BranchCode", r.BranchCode) +
            El("BankAccountNumber", r.BankAccountNumber) +
            El("BankAccountType", r.BankAccountType == BankAccountType.Savings ? "Savings" : "Current") +
            El("MobileNumber", r.MobileNumber) +
            El("EmailAddress", r.EmailAddress ?? "") +
            El("CollectionAmount", r.CollectionAmount.ToString("0.00", CultureInfo.InvariantCulture)) +
            El("FirstCollectionDiffers", r.FirstCollectionDiffers ? "true" : "false") +
            (r.FirstCollectionDiffers && r.FirstCollectionAmount is { } first
                ? El("FirstCollectionAmount", first.ToString("0.00", CultureInfo.InvariantCulture)) : "") +
            (r.FirstCollectionDate is { } date ? El("FirstCollectionDate", date.ToString("yyyyMMdd")) : "") +
            (dayCode is null ? "" : El("collectionDayCode", dayCode)) +
            El("DebtorIdentificationCode", r.IsIdNumber ? "I" : "P") +
            "</DebiCheckAuthenticate>";
    }

    /// <summary>
    /// Netcash's collection day code, e.g. MNTH_25. Accepts one already in that form, a bare day
    /// ("25") or, failing both, the first collection date's day. Null when there is nothing to go on.
    /// </summary>
    public static string? DayCode(string? code, DateTime? firstCollection)
    {
        var c = code?.Trim().ToUpperInvariant();
        if (c is not null && Regex.IsMatch(c, @"^(WEEK|FRTN|MNTH|QURT)_\d{2}$")) return c;
        if (c is not null && int.TryParse(c, out var day) && day is >= 1 and <= 31) return $"MNTH_{day:D2}";
        return firstCollection is { } d ? $"MNTH_{d.Day:D2}" : null;
    }

    public static NetcashResult<DebiCheckAuthenticateResponse> ParseAuthenticate(string xml)
    {
        if (!TryParse(xml, out var doc, out var fault)) return NetcashResult<DebiCheckAuthenticateResponse>.Fail(NetcashCodes.GeneralServiceError, fault);

        var r = doc!.Descendants(XName.Get("DebiCheckAuthenticateResult", Tempuri)).FirstOrDefault();
        if (r is null) return NetcashResult<DebiCheckAuthenticateResponse>.Fail(NetcashCodes.GeneralServiceError, "Netcash's answer had no DebiCheck result in it.");

        XNamespace n = Niws;
        string? V(string name) => r.Element(n + name)?.Value.Trim() is { Length: > 0 } v ? v : null;
        var messages = Messages(r);

        var data = new DebiCheckAuthenticateResponse
        {
            ContractReference = V("ContractReference"),
            BankResponseCode = V("BankResponseCode"),
            BankservResponseCode = V("BankservResponseCode"),
            ClientResponseCode = V("ClientResponseCode"),
            Status = V("Status")
        };

        // Refused before reaching the bank: no reference, no bank codes, only messages.
        if (data.ContractReference is null && data.BankResponseCode is null && data.Status is null)
        {
            var code = messages.Select(m => Regex.Match(m, @"\b(\d{3})\b")).FirstOrDefault(m => m.Success)?.Groups[1].Value
                       ?? NetcashCodes.Failed;
            return new NetcashResult<DebiCheckAuthenticateResponse>
            {
                Success = false,
                Code = code,
                Message = messages.Count > 0
                    ? "Netcash did not send the mandate: " + string.Join("; ", messages) + (code == NetcashCodes.NonRealTimeTemplate ? " " + NetcashCodes.Describe(code) : "")
                    : "Netcash did not send the mandate and gave no reason.",
                Data = data
            };
        }

        return new NetcashResult<DebiCheckAuthenticateResponse>
        {
            Success = true,
            Code = NetcashCodes.Success,
            Message = messages.Count > 0 ? string.Join("; ", messages) : null,
            Data = data
        };
    }

    // ---------------------------------------------------------------- status and cancel

    public async Task<NetcashResult<string>> GetAuthenticationStatusAsync(string debitOrderServiceKey, string contractReference, CancellationToken ct = default)
    {
        var key = await KeyAsync(debitOrderServiceKey, ct);
        if (key is null) return MissingKey<string>();

        var (xml, error) = await PostAsync("DebiCheckAuthenticationCurrentStatus",
            $"<DebiCheckAuthenticationCurrentStatus xmlns=\"{Tempuri}\">{El("ServiceKey", key)}{El("ContractReference", contractReference)}</DebiCheckAuthenticationCurrentStatus>",
            _options.DefaultTimeoutSeconds, ct);
        if (error is not null) return NetcashResult<string>.Fail(NetcashCodes.GeneralServiceError, error);

        return ParseStatus(xml!);
    }

    /// <summary>The mandate's status as Netcash words it, with the bank's codes and any cancellation in the message.</summary>
    public static NetcashResult<string> ParseStatus(string xml)
    {
        if (!TryParse(xml, out var doc, out var fault)) return NetcashResult<string>.Fail(NetcashCodes.GeneralServiceError, fault);

        var r = doc!.Descendants(XName.Get("DebiCheckAuthenticationCurrentStatusResult", Tempuri)).FirstOrDefault();
        XNamespace n = Niws;
        string? V(string name) => r?.Element(n + name)?.Value.Trim() is { Length: > 0 } v ? v : null;

        var status = V("Status");
        if (status is null) return NetcashResult<string>.Fail(NetcashCodes.Failed, "Netcash has no status for that contract reference.");

        var details = new[]
        {
            V("BankservResponse") is { } bs ? $"Bankserv {bs}" : null,
            V("BankResponse") is { } b ? $"bank {b}" : null,
            V("ClientResponse") is { } c ? $"client {c}" : null,
            V("DateCancelled") is { } dc ? $"cancelled {dc}" + (V("CancellationReason") is { } why ? $" ({why})" : "") : null,
            V("UpdateDate") is { } u ? $"updated {u}" : null
        }.Where(x => x is not null);

        return new NetcashResult<string> { Success = true, Code = NetcashCodes.Success, Data = status, Message = string.Join(", ", details) };
    }

    public async Task<NetcashResult<bool>> CancelAuthenticationAsync(string debitOrderServiceKey, string contractReference, string reasonCode, CancellationToken ct = default)
    {
        var key = await KeyAsync(debitOrderServiceKey, ct);
        if (key is null) return MissingKey<bool>();

        var reason = CancellationReasons.Contains(reasonCode?.Trim().ToUpperInvariant()) ? reasonCode!.Trim().ToUpperInvariant() : "CUST";
        var (xml, error) = await PostAsync("DebiCheckCancelAuthentication",
            $"<DebiCheckCancelAuthentication xmlns=\"{Tempuri}\">{El("ServiceKey", key)}{El("ContractReference", contractReference)}{El("reasonCode", reason)}</DebiCheckCancelAuthentication>",
            _options.DefaultTimeoutSeconds, ct);
        if (error is not null) return NetcashResult<bool>.Fail(NetcashCodes.GeneralServiceError, error);

        if (!TryParse(xml!, out var doc, out var fault)) return NetcashResult<bool>.Fail(NetcashCodes.GeneralServiceError, fault);
        var answer = doc!.Descendants(XName.Get("DebiCheckCancelAuthenticationResult", Tempuri)).FirstOrDefault()?.Value.Trim() ?? "";
        _log.LogInformation("Netcash DebiCheck cancel for {Reference} answered {Answer}.", contractReference, answer.Length <= 40 ? answer : answer[..40]);

        return answer is "000" or "0" || answer.Contains("success", StringComparison.OrdinalIgnoreCase)
            ? NetcashResult<bool>.Ok(true, NetcashCodes.Success)
            : NetcashResult<bool>.Fail(answer.Length <= 3 ? answer : NetcashCodes.Failed, $"Netcash did not cancel the mandate: {answer}");
    }

    // ---------------------------------------------------------------- templates

    /// <summary>
    /// The account's mandate templates. Netcash has no "list" call, so the ids come from settings
    /// (<c>Netcash:MandateTemplates:&lt;account&gt;</c>, comma separated) and each is confirmed with
    /// DebiCheckRetrieveMandateTemplateDetail; only the ones Netcash recognises are offered.
    /// </summary>
    public async Task<NetcashResult<IReadOnlyList<string>>> RetrieveMandateTemplatesAsync(string debitOrderServiceKey, CancellationToken ct = default)
    {
        var account = await _keys.AccountNumberAsync(ct);
        var configured = (_configuration[$"Netcash:MandateTemplates:{account}"] ?? _configuration["Netcash:MandateTemplates:Default"] ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (configured.Length == 0)
            return NetcashResult<IReadOnlyList<string>>.Fail(NetcashCodes.ParameterError,
                "No DebiCheck mandate template is set up for this Netcash account. Find its id (NCDCT...) in the Netcash portal under DebiCheck.");

        var key = await KeyAsync(debitOrderServiceKey, ct);
        if (key is null) return MissingKey<IReadOnlyList<string>>();

        var confirmed = new List<string>();
        foreach (var id in configured)
        {
            var (xml, error) = await PostAsync("DebiCheckRetrieveMandateTemplateDetail",
                $"<DebiCheckRetrieveMandateTemplateDetail xmlns=\"{Tempuri}\">{El("ServiceKey", key)}{El("DebiCheckMandateTemplateId", id)}</DebiCheckRetrieveMandateTemplateDetail>",
                _options.DefaultTimeoutSeconds, ct);
            if (error is not null) return NetcashResult<IReadOnlyList<string>>.Fail(NetcashCodes.GeneralServiceError, error);

            var detail = TemplateDetail(xml!);
            _log.LogInformation("Netcash mandate template {Id}: {Found}.", id, detail is null ? "not recognised" : $"{detail.Value.Name}, {detail.Value.Authentication}");
            if (detail is not null) confirmed.Add(id);
        }

        return confirmed.Count > 0
            ? NetcashResult<IReadOnlyList<string>>.Ok(confirmed)
            : NetcashResult<IReadOnlyList<string>>.Fail(NetcashCodes.ParameterError,
                $"Netcash does not recognise the mandate template(s) {string.Join(", ", configured)} on this account.");
    }

    /// <summary>A template's name and authentication type, or null when Netcash does not recognise the id.</summary>
    public static (string Name, string Authentication)? TemplateDetail(string xml)
    {
        if (!TryParse(xml, out var doc, out _)) return null;
        var r = doc!.Descendants(XName.Get("DebiCheckRetrieveMandateTemplateDetailResult", Tempuri)).FirstOrDefault();
        XNamespace n = Niws;
        var id = r?.Element(n + "TemplateId")?.Value.Trim();
        if (string.IsNullOrEmpty(id)) return null;
        return (r!.Element(n + "TemplateName")?.Value.Trim() ?? id, r.Element(n + "AuthenticationType")?.Value.Trim() ?? "");
    }

    // ---------------------------------------------------------------- not connected yet

    public Task<NetcashResult<string>> AuthenticateBatchAsync(string debitOrderServiceKey, IReadOnlyList<DebiCheckAuthenticateRequest> requests,
        DateTime actionDate, string batchName, CancellationToken ct = default) =>
        Task.FromResult(NetcashResult<string>.Fail(NetcashCodes.Failed,
            "Batch (TT2) mandates are not connected to Netcash yet. Use real-time (TT1) for now."));

    public Task<NetcashResult<bool>> AmendAuthenticationAsync(string debitOrderServiceKey, string contractReference, decimal newAmount,
        CancellationToken ct = default) =>
        Task.FromResult(NetcashResult<bool>.Fail(NetcashCodes.Failed, "Amending a mandate is not connected to Netcash yet."));

    // ---------------------------------------------------------------- plumbing

    /// <summary>The business's real debit-order key; a caller's key is used only if it looks like one (never a placeholder).</summary>
    private async Task<string?> KeyAsync(string passed, CancellationToken ct) =>
        await _keys.KeyAsync(NetcashServiceId.DebitOrders, ct)
        ?? (Guid.TryParse(passed, out _) ? passed : null);

    private async Task<(string? Xml, string? Error)> PostAsync(string operation, string body, int timeoutSeconds, CancellationToken ct)
    {
        var envelope = $"<s:Envelope xmlns:s=\"{Soap11}\"><s:Body>{body}</s:Body></s:Envelope>";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.NifEndpoint) { Content = new StringContent(envelope, Encoding.UTF8) };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"http://tempuri.org/INIWS_NIF/{operation}\"");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await Http.SendAsync(request, cts.Token);
            var text = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode && !text.Contains("Envelope", StringComparison.Ordinal))
            {
                _log.LogWarning("Netcash {Operation} returned HTTP {Status}.", operation, (int)response.StatusCode);
                return (null, $"Netcash answered with HTTP {(int)response.StatusCode}. Try again shortly.");
            }
            return (text, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Netcash {Operation} timed out after {Seconds}s.", operation, timeoutSeconds);
            return (null, operation == "DebiCheckAuthenticate"
                ? "The bank did not answer in time. The account holder may still approve it - check the mandate's status before sending again."
                : "Netcash did not answer in time. Try again shortly.");
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("Netcash {Operation} unreachable: {Message}", operation, ex.Message);
            return (null, "Could not reach Netcash. Check the connection and try again.");
        }
    }

    private static bool TryParse(string xml, out XDocument? doc, out string error)
    {
        error = "";
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException)
        {
            doc = null;
            error = "Netcash sent an answer AM-Pay could not read.";
            return false;
        }

        var fault = doc.Descendants(XName.Get("Fault", Soap11)).FirstOrDefault();
        if (fault is null) return true;

        error = "Netcash rejected the request: " + (fault.Element("faultstring")?.Value ?? "SOAP fault");
        return false;
    }

    private static List<string> Messages(XElement result) =>
        result.Element(XName.Get("Messages", Niws))?.Elements(XName.Get("string", ArraysNs))
            .Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList() ?? new List<string>();

    private static string El(string name, string value) => $"<{name}>{SecurityElement.Escape(value)}</{name}>";

    private static NetcashResult<T> MissingKey<T>() => NetcashResult<T>.Fail(NetcashCodes.NoActiveServiceKey,
        "This business's Netcash debit order key is not loaded, so nothing was sent. Load it on the Netcash service keys screen.");
}
