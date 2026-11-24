using System.Globalization;
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
/// The debit order surface of NIWS_NIF with the eMandate (AddMandate) live. Batch uploads and
/// action dates are still the stub's until they are built; they are passed through to it.
/// <para>
/// AddMandate is sent exactly as the service's WSDL declares it: a SOAP 1.1 call whose
/// parameters are elements in the tempuri namespace, in the WSDL's order, enums by name.
/// </para>
/// </summary>
public class LiveNetcashDebitOrderService : INetcashDebitOrderService
{
    private const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Tempuri = "http://tempuri.org/";
    private const string Niws = "http://schemas.datacontract.org/2004/07/NC.DG.TMS.C.WCF.NIWS";
    private const string ArraysNs = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) });

    private readonly NetcashOptions _options;
    private readonly INetcashKeyProvider _keys;
    private readonly StubNetcashDebitOrderService _stub;
    private readonly ILogger<LiveNetcashDebitOrderService> _log;

    public LiveNetcashDebitOrderService(IOptions<NetcashOptions> options, INetcashKeyProvider keys,
        StubNetcashDebitOrderService stub, ILogger<LiveNetcashDebitOrderService> log)
    {
        _options = options.Value;
        _keys = keys;
        _stub = stub;
        _log = log;
    }

    public Task<NetcashResult<string>> UploadMasterfileAsync(string key, string nif, CancellationToken ct = default) => _stub.UploadMasterfileAsync(key, nif, ct);
    public Task<NetcashResult<string>> UploadCollectionBatchAsync(string key, string nif, CancellationToken ct = default) => _stub.UploadCollectionBatchAsync(key, nif, ct);
    public Task<NetcashResult<string>> GetLoadReportAsync(string key, string token, CancellationToken ct = default) => _stub.GetLoadReportAsync(key, token, ct);
    public Task<NetcashResult<DateTime>> RequestActionDateAsync(string key, DateTime desired, CancellationToken ct = default) => _stub.RequestActionDateAsync(key, desired, ct);

    public async Task<NetcashResult<AddMandateResponse>> AddMandateAsync(string debitOrderServiceKey, AddMandateRequest r, CancellationToken ct = default)
    {
        var key = string.IsNullOrWhiteSpace(debitOrderServiceKey) ? await _keys.KeyAsync(NetcashServiceId.DebitOrders, ct) : debitOrderServiceKey;
        if (string.IsNullOrWhiteSpace(key))
            return NetcashResult<AddMandateResponse>.Fail(NetcashCodes.NoActiveServiceKey,
                "This business's Netcash debit order key is not loaded, so nothing was sent. Load it on the Netcash service keys screen.");

        var body = new StringBuilder($"<AddMandate xmlns=\"{Tempuri}\">");
        void E(string name, string? value) { if (value is not null) body.Append($"<{name}>{SecurityElement.Escape(value)}</{name}>"); }
        static string B(bool b) => b ? "true" : "false";
        var inv = CultureInfo.InvariantCulture;

        // Order follows the WSDL sequence.
        E("ServiceKey", key);
        E("AccountReference", r.AccountReference);
        E("MandateName", r.MandateName);
        E("MandateAmount", r.MandateAmount.ToString("0.00", inv));
        E("IsConsumer", B(r.IsConsumer));
        E("FirstName", r.FirstName);
        E("Surname", r.Surname);
        E("TradingName", r.TradingName ?? "");
        E("RegistrationNumber", r.IsConsumer ? r.IdNumber ?? "" : r.RegistrationNumber ?? "");
        E("RegisteredName", r.RegisteredName ?? "");
        E("MobileNumber", r.MobileNumber);
        E("DebitFrequency", r.DebitFrequency.ToString());
        E("CommencementMonth", r.CommencementMonth.ToString(inv));
        E("CommencementDay", r.CommencementDay);
        E("AgreementDate", r.AgreementDate.ToString("yyyyMMdd", inv));
        E("AgreementReferenceNumber", r.AgreementReferenceNumber);
        E("Notes", r.Notes);
        E("Field1", r.Field1);
        E("BankDetailType", ((int)r.BankDetailType).ToString(inv));
        E("BankAccountName", r.BankAccountName);
        E("BankAccountNumber", r.BankAccountNumber);
        E("BranchCode", r.BranchCode);
        E("BankAccountType", r.BankAccountType == BankAccountType.Savings ? "Savings" : "Current");
        if (r.IsConsumer && !string.IsNullOrEmpty(r.IdNumber)) E("IsIdNumber", "true");
        E("EmailAddress", r.EmailAddress);
        E("MandateActive", B(r.MandateActive));
        E("AddToMasterFile", B(r.AddToMasterFile));
        body.Append("</AddMandate>");

        var (xml, error) = await PostAsync("AddMandate", body.ToString(), ct);
        if (error is not null) return NetcashResult<AddMandateResponse>.Fail(NetcashCodes.GeneralServiceError, error);

        try
        {
            var doc = XDocument.Parse(xml!);
            var fault = doc.Descendants(XName.Get("Fault", Soap11)).FirstOrDefault();
            if (fault is not null)
                return NetcashResult<AddMandateResponse>.Fail(NetcashCodes.GeneralServiceError,
                    "Netcash rejected the request: " + (fault.Element("faultstring")?.Value ?? "SOAP fault"));

            var result = doc.Descendants(XName.Get("AddMandateResult", Tempuri)).First();
            string? V(string n) => result.Element(XName.Get(n, Niws))?.Value;
            List<string> L(string n) => result.Element(XName.Get(n, Niws))?.Elements(XName.Get("string", ArraysNs))
                .Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList() ?? new();

            var response = new AddMandateResponse { MandateUrl = V("MandateUrl"), Errors = L("Errors"), Warnings = L("Warnings") };
            var code = V("ErrorCode") ?? "";
            _log.LogInformation("Netcash AddMandate for {Reference}: code {Code}, {Errors} error(s).", r.AccountReference, code, response.Errors.Count);

            if (code == "000" && !string.IsNullOrEmpty(response.MandateUrl)) return NetcashResult<AddMandateResponse>.Ok(response);

            var why = response.Errors.Count > 0 ? string.Join("; ", response.Errors)
                : code == "100" ? "Netcash did not accept the debit order service key."
                : code == "200" ? "Netcash had an internal error. Try again later or contact Netcash support."
                : $"Netcash answered with code {code}.";
            return NetcashResult<AddMandateResponse>.Fail(code, "Netcash did not create the eMandate: " + why);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            _log.LogWarning("Netcash AddMandate answer could not be read: {Message}", ex.Message);
            return NetcashResult<AddMandateResponse>.Fail(NetcashCodes.GeneralServiceError, "Netcash's answer could not be read. Check the mandate in the Netcash portal before sending again.");
        }
    }

    private async Task<(string? Xml, string? Error)> PostAsync(string operation, string body, CancellationToken ct)
    {
        var envelope = $"<s:Envelope xmlns:s=\"{Soap11}\"><s:Body>{body}</s:Body></s:Envelope>";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.NifEndpoint) { Content = new StringContent(envelope, Encoding.UTF8) };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"http://tempuri.org/INIWS_NIF/{operation}\"");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await Http.SendAsync(request, cts.Token);
            var text = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode && !text.Contains("Envelope", StringComparison.Ordinal))
                return (null, $"Netcash answered with HTTP {(int)response.StatusCode}. Try again shortly.");
            return (text, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "Netcash did not answer in time. Check the Netcash portal before sending again.");
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("Netcash {Operation} unreachable: {Message}", operation, ex.Message);
            return (null, "Netcash could not be reached. Try again shortly.");
        }
    }
}
