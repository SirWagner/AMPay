using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml.Linq;
using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AMPay.Infrastructure.Netcash;

/// <summary>
/// The real NIWS_Partner.ValidateServiceKey call: SOAP 1.2 over HTTPS with WS-Addressing, as
/// the service's WSDL (WSHttpBinding, transport security) requires. Written by hand rather than
/// generated, so it needs no WCF packages and the exact envelope is visible here.
/// <para>
/// Netcash locks a merchant account after three failed attempts inside ten minutes, and only a
/// successful call clears it. This class therefore refuses a third attempt itself: after two
/// failures for an account it answers "wait" until ten minutes have passed since the first.
/// </para>
/// <para>
/// Service keys are never logged. The response echoes them back, so the raw XML is not kept.
/// </para>
/// </summary>
public class LiveNetcashPartnerService : INetcashPartnerService
{
    public const string Action = "http://tempuri.org/INIWS_Partner/ValidateServiceKey";

    private const string Soap = "http://www.w3.org/2003/05/soap-envelope";
    private const string Addressing = "http://www.w3.org/2005/08/addressing";
    private const string Tempuri = "http://tempuri.org/";
    private const string Niws = "http://schemas.datacontract.org/2004/07/NC.DG.TMS.C.WCF.NIWS";

    /// <summary>Netcash locks at three; we stop at two so a mistake never costs the account.</summary>
    public const int MaxFailures = 2;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

    // Shared across requests (and so across scopes): the guard must see every attempt.
    private static readonly ConcurrentDictionary<string, List<DateTime>> Failures = new();

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) });

    private readonly NetcashOptions _options;
    private readonly INetcashSecretStore _secrets;
    private readonly ILogger<LiveNetcashPartnerService> _log;
    private readonly Func<DateTime> _clock;

    public LiveNetcashPartnerService(
        IOptions<NetcashOptions> options, INetcashSecretStore secrets, ILogger<LiveNetcashPartnerService> log,
        Func<DateTime>? clock = null)
    {
        _options = options.Value;
        _secrets = secrets;
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public async Task<NetcashResult<IReadOnlyList<ServiceKeyValidation>>> ValidateServiceKeysAsync(
        string merchantAccountNumber,
        IReadOnlyDictionary<NetcashServiceId, string> serviceKeys,
        CancellationToken ct = default)
    {
        var account = merchantAccountNumber.Trim();

        if (BlockedUntil(account, _clock()) is { } until)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((until - _clock()).TotalMinutes));
            return NetcashResult<IReadOnlyList<ServiceKeyValidation>>.Fail(NetcashCodes.AccountLockedOut,
                $"Two validation attempts for this account have failed. To avoid Netcash locking the account, " +
                $"AM-Pay will not try again for {minutes} minute(s). Check the account number and keys meanwhile.");
        }

        var vendorKey = await _secrets.GetSoftwareVendorKeyAsync(ct);
        var envelope = BuildEnvelope(_options.PartnerEndpoint, account, vendorKey, serviceKeys);

        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.PartnerEndpoint)
            {
                Content = new StringContent(envelope, Encoding.UTF8)
            };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"application/soap+xml; charset=utf-8; action=\"{Action}\"");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds));
            using var response = await Http.SendAsync(request, cts.Token);
            body = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode && !body.Contains("Envelope", StringComparison.Ordinal))
            {
                _log.LogWarning("Netcash ValidateServiceKey returned HTTP {Status}.", (int)response.StatusCode);
                return NetcashResult<IReadOnlyList<ServiceKeyValidation>>.Fail(NetcashCodes.GeneralServiceError,
                    $"Netcash answered with HTTP {(int)response.StatusCode}. Nothing was validated; try again shortly.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("Netcash ValidateServiceKey unreachable: {Message}", ex.Message);
            return NetcashResult<IReadOnlyList<ServiceKeyValidation>>.Fail(NetcashCodes.GeneralServiceError,
                "Could not reach Netcash. Nothing was validated; check the connection and try again.");
        }

        var result = ParseResponse(body, serviceKeys.Keys);

        // A transport fault is not a failed attempt; an answered request that was refused is.
        if (result.Success && result.Data!.All(v => v.Status == ServiceKeyStatus.Validated))
            Failures.TryRemove(account, out _);
        else if (result.Code != NetcashCodes.GeneralServiceError)
            RecordFailure(account, _clock());

        _log.LogInformation("Netcash ValidateServiceKey for {Account}: account status {Code}; {Summary}.",
            Mask(account), result.Code,
            result.Data is null ? "no service results" : string.Join(", ", result.Data.Select(v => $"{(int)v.ServiceId}={v.Status}")));

        return result;
    }

    // ---------------------------------------------------------------- envelope

    public static string BuildEnvelope(string endpoint, string merchantAccount, string vendorKey,
        IReadOnlyDictionary<NetcashServiceId, string> serviceKeys)
    {
        static string E(string? s) => SecurityElement.Escape(s ?? "");

        var services = string.Concat(serviceKeys.Select(k =>
            $"<n:ServiceInfo><n:ServiceId>{(int)k.Key}</n:ServiceId><n:ServiceKey>{E(k.Value)}</n:ServiceKey></n:ServiceInfo>"));

        return
            $"<s:Envelope xmlns:s=\"{Soap}\" xmlns:a=\"{Addressing}\">" +
            "<s:Header>" +
            $"<a:Action s:mustUnderstand=\"1\">{Action}</a:Action>" +
            $"<a:MessageID>urn:uuid:{Guid.NewGuid()}</a:MessageID>" +
            "<a:ReplyTo><a:Address>http://www.w3.org/2005/08/addressing/anonymous</a:Address></a:ReplyTo>" +
            $"<a:To s:mustUnderstand=\"1\">{E(endpoint)}</a:To>" +
            "</s:Header>" +
            "<s:Body>" +
            $"<ValidateServiceKey xmlns=\"{Tempuri}\">" +
            $"<request xmlns:n=\"{Niws}\">" +
            $"<n:MerchantAccount>{E(merchantAccount)}</n:MerchantAccount>" +
            $"<n:ServiceInfoList>{services}</n:ServiceInfoList>" +
            $"<n:SoftwareVendorKey>{E(vendorKey)}</n:SoftwareVendorKey>" +
            "</request>" +
            "</ValidateServiceKey>" +
            "</s:Body>" +
            "</s:Envelope>";
    }

    /// <summary>
    /// Reads the answer: AccountStatus 001 means the account and vendor key authenticated; each
    /// ServiceStatus is 001 validated, 105 no active service, 106 no active key. A SOAP fault
    /// becomes a general error carrying the fault's text (which never contains a key).
    /// </summary>
    public static NetcashResult<IReadOnlyList<ServiceKeyValidation>> ParseResponse(string body, IEnumerable<NetcashServiceId> asked)
    {
        XDocument doc;
        try { doc = XDocument.Parse(body); }
        catch (System.Xml.XmlException)
        {
            return NetcashResult<IReadOnlyList<ServiceKeyValidation>>.Fail(NetcashCodes.GeneralServiceError,
                "Netcash sent an answer AM-Pay could not read.");
        }

        XNamespace s = Soap;
        XNamespace n = Niws;

        var fault = doc.Descendants(s + "Fault").FirstOrDefault();
        if (fault is not null)
        {
            var reason = fault.Descendants(s + "Text").FirstOrDefault()?.Value ?? "SOAP fault";
            return NetcashResult<IReadOnlyList<ServiceKeyValidation>>.Fail(NetcashCodes.GeneralServiceError,
                $"Netcash rejected the request: {reason}");
        }

        var resultEl = doc.Descendants(XName.Get("ValidateServiceKeyResult", Tempuri)).FirstOrDefault();
        if (resultEl is null)
            return NetcashResult<IReadOnlyList<ServiceKeyValidation>>.Fail(NetcashCodes.GeneralServiceError,
                "Netcash's answer had no validation result in it.");

        var accountStatus = resultEl.Element(n + "AccountStatus")?.Value?.Trim() ?? "";

        var byService = resultEl.Descendants(n + "ServiceInfoResponse")
            .Select(e => new
            {
                Id = int.TryParse(e.Element(n + "ServiceId")?.Value, out var id) ? id : -1,
                Status = e.Element(n + "ServiceStatus")?.Value?.Trim() ?? ""
            })
            .ToList();

        var validations = asked.Select(id =>
        {
            var hit = byService.FirstOrDefault(x => x.Id == (int)id);
            var code = hit?.Status ?? "";
            return new ServiceKeyValidation
            {
                ServiceId = id,
                Status = StatusFor(code),
                Message = hit is null ? "Netcash did not report on this service." : $"{code}: {NetcashCodes.Describe(code)}"
            };
        }).ToList();

        if (accountStatus != NetcashCodes.Authenticated)
            return new NetcashResult<IReadOnlyList<ServiceKeyValidation>>
            {
                Success = false,
                Code = accountStatus == "" ? NetcashCodes.GeneralServiceError : accountStatus,
                Message = $"Netcash did not authenticate the account ({(accountStatus == "" ? "no status" : accountStatus)}): " +
                          NetcashCodes.Describe(accountStatus),
                Data = validations
            };

        return NetcashResult<IReadOnlyList<ServiceKeyValidation>>.Ok(validations, accountStatus);
    }

    private static ServiceKeyStatus StatusFor(string code) => code switch
    {
        NetcashCodes.Authenticated => ServiceKeyStatus.Validated,
        NetcashCodes.NoActiveService => ServiceKeyStatus.NoActiveService,
        NetcashCodes.NoActiveServiceKey => ServiceKeyStatus.NoActiveServiceKey,
        NetcashCodes.AuthenticationFailure => ServiceKeyStatus.AuthenticationFailed,
        NetcashCodes.AccountLockedOut => ServiceKeyStatus.AccountLockedOut,
        _ => ServiceKeyStatus.ServiceError
    };

    // ---------------------------------------------------------------- lockout guard

    public static DateTime? BlockedUntil(string account, DateTime now)
    {
        if (!Failures.TryGetValue(account, out var list)) return null;
        lock (list)
        {
            list.RemoveAll(t => now - t > FailureWindow);
            return list.Count >= MaxFailures ? list.Min() + FailureWindow : null;
        }
    }

    public static void RecordFailure(string account, DateTime now)
    {
        var list = Failures.GetOrAdd(account, _ => new List<DateTime>());
        lock (list) list.Add(now);
    }

    /// <summary>For tests: forget every recorded failure.</summary>
    public static void ResetGuard() => Failures.Clear();

    private static string Mask(string account) => account.Length <= 4 ? "****" : new string('*', account.Length - 4) + account[^4..];
}
