using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AMPay.Infrastructure.Netcash;

/// <summary>
/// Credit bureau reports through Netcash risk reports (NIWS_NIF, SOAP 1.1):
/// <list type="number">
/// <item>BatchFileUpload with instruction CD11 (TransUnion), CD12 (Experian) or CD13 (XDS) and one
/// T record: account reference (101), ID number (111), surname (113), first name (114) and the
/// enquiry reason (450). Netcash answers with a file token.</item>
/// <item>RequestCreditDataReport with that token returns the bureau's report as a base64 PDF, or
/// "FILE NOT READY" until the bureau has answered.</item>
/// </list>
/// Uses the business's Risk Services key. Every enquiry leaves a footprint on the client's credit
/// record and may be billed, so it is only ever sent on an explicit request with the client's
/// consent recorded. Keys and ID numbers are never logged.
/// </summary>
public class NetcashRiskReportService : ICreditBureauClient
{
    private const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Tempuri = "http://tempuri.org/";
    private const string ArraysNs = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";
    public const string NotReady = "FILE NOT READY";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) });

    private readonly NetcashOptions _options;
    private readonly INetcashKeyProvider _keys;
    private readonly INetcashSecretStore _secrets;
    private readonly ILogger<NetcashRiskReportService> _log;

    public NetcashRiskReportService(IOptions<NetcashOptions> options, INetcashKeyProvider keys, INetcashSecretStore secrets,
        ILogger<NetcashRiskReportService> log)
    {
        _options = options.Value;
        _keys = keys;
        _secrets = secrets;
        _log = log;
    }

    public async Task<NetcashResult<string>> SubmitAsync(CreditCheckRequest request, CancellationToken ct = default)
    {
        var key = await _keys.KeyAsync(NetcashServiceId.RiskReports, ct);
        if (key is null) return MissingKey<string>();

        var file = BuildFile(key, await _secrets.GetSoftwareVendorKeyAsync(ct), request, DateTime.Today);

        var (xml, error) = await PostAsync("BatchFileUpload", $"{El("ServiceKey", key)}{El("File", file)}", ct);
        if (error is not null) return NetcashResult<string>.Fail(NetcashCodes.GeneralServiceError, error);

        var answer = Result(xml!, "BatchFileUploadResult");
        _log.LogInformation("Netcash risk report {Bureau} for {Reference}: {Answer}.", request.Bureau, request.AccountReference,
            answer is null ? "no answer" : IsErrorCode(answer) ? $"code {answer}" : "file token received");

        if (string.IsNullOrWhiteSpace(answer))
            return NetcashResult<string>.Fail(NetcashCodes.GeneralServiceError, "Netcash did not return a file token.");
        if (IsErrorCode(answer))
            return NetcashResult<string>.Fail(answer, $"Netcash refused the credit check ({answer}): {NetcashCodes.Describe(answer)}");

        return NetcashResult<string>.Ok(answer.Trim());
    }

    /// <summary>The batch file for one enquiry, in the risk-report layout.</summary>
    public static string BuildFile(string serviceKey, string vendorKey, CreditCheckRequest r, DateTime actionDate) =>
        new NifFileBuilder(serviceKey, vendorKey, Instruction(r.Bureau), Limit($"CC-{r.AccountReference}", 30), actionDate)
            .AddTransaction(t => t
                .Set(NifField.AccountReference, Limit(r.AccountReference, 22))
                .Set(NifField.IdNumber, r.IdNumber.Trim())
                .Set(NifField.Surname, Clean(r.Surname))
                .Set(NifField.FirstName, Clean(r.FirstName))
                .Set(NifField.EnquiryReason, r.ReasonCode))
            .Build();

    public static string Instruction(CreditBureau bureau) => bureau switch
    {
        CreditBureau.Experian => NifInstruction.ExperianCreditCheck,
        CreditBureau.Xds => NifInstruction.XdsCreditCheck,
        _ => NifInstruction.TransUnionCreditCheck
    };

    public async Task<NetcashResult<byte[]>> FetchReportAsync(string fileToken, CancellationToken ct = default)
    {
        var key = await _keys.KeyAsync(NetcashServiceId.RiskReports, ct);
        if (key is null) return MissingKey<byte[]>();

        var (xml, error) = await PostAsync("RequestCreditDataReport", $"{El("ServiceKey", key)}{El("FileToken", fileToken)}", ct);
        if (error is not null) return NetcashResult<byte[]>.Fail(NetcashCodes.GeneralServiceError, error);

        var report = ParseReport(xml!);
        if (report.Success && report.Data is null)
        {
            // Not ready - but a batch that failed to load never will be. The load report says which.
            var (loadXml, loadError) = await PostAsync("RequestFileUploadReport", $"{El("ServiceKey", key)}{El("FileToken", fileToken)}", ct);
            if (loadError is null && Result(loadXml!, "RequestFileUploadReportResult") is { } load &&
                !load.Contains(NotReady, StringComparison.OrdinalIgnoreCase) &&
                !load.Contains("SUCCESSFUL", StringComparison.OrdinalIgnoreCase) && load.Trim().Length > 0)
            {
                return NetcashResult<byte[]>.Fail(NetcashCodes.Failed, "Netcash could not load the enquiry: " + Limit(load.Trim(), 300));
            }
        }
        return report;
    }

    /// <summary>
    /// The PDF from a RequestCreditDataReport answer (an array of strings: the base64 may arrive in
    /// one piece or several). "FILE NOT READY" is success with no data; anything that does not
    /// decode to a PDF is a failure carrying Netcash's words.
    /// </summary>
    public static NetcashResult<byte[]> ParseReport(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return NetcashResult<byte[]>.Fail(NetcashCodes.GeneralServiceError, "Netcash sent an answer AM-Pay could not read."); }

        if (doc.Descendants(XName.Get("Fault", Soap11)).FirstOrDefault() is { } fault)
            return NetcashResult<byte[]>.Fail(NetcashCodes.GeneralServiceError, "Netcash rejected the request: " + (fault.Element("faultstring")?.Value ?? "SOAP fault"));

        var result = doc.Descendants(XName.Get("RequestCreditDataReportResult", Tempuri)).FirstOrDefault();
        var parts = result?.Elements(XName.Get("string", ArraysNs)).Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList() ?? new();
        var joined = string.Concat(parts);

        if (joined.Length == 0 || joined.Contains(NotReady, StringComparison.OrdinalIgnoreCase))
            return NetcashResult<byte[]>.Ok(null!, "NOTREADY");

        if (IsErrorCode(joined))
            return NetcashResult<byte[]>.Fail(joined, $"Netcash could not return the report ({joined}): {NetcashCodes.Describe(joined)}");

        try
        {
            var bytes = Convert.FromBase64String(joined);
            if (bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F')
                return NetcashResult<byte[]>.Ok(bytes);
        }
        catch (FormatException) { }

        return NetcashResult<byte[]>.Fail(NetcashCodes.Failed, "Netcash answered, but not with a report: " + Limit(joined, 200));
    }

    // ---------------------------------------------------------------- plumbing

    private async Task<(string? Xml, string? Error)> PostAsync(string operation, string parameters, CancellationToken ct)
    {
        var envelope = $"<s:Envelope xmlns:s=\"{Soap11}\"><s:Body><{operation} xmlns=\"{Tempuri}\">{parameters}</{operation}></s:Body></s:Envelope>";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.NifEndpoint) { Content = new StringContent(envelope, Encoding.UTF8) };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"http://tempuri.org/INIWS_NIF/{operation}\"");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds));
            using var response = await Http.SendAsync(request, cts.Token);
            var text = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode && !text.Contains("Envelope", StringComparison.Ordinal))
                return (null, $"Netcash answered with HTTP {(int)response.StatusCode}. Try again shortly.");
            return (text, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("Netcash {Operation} unreachable: {Message}", operation, ex.Message);
            return (null, "Could not reach Netcash. Try again shortly.");
        }
    }

    private static string? Result(string xml, string element)
    {
        try { return XDocument.Parse(xml).Descendants(XName.Get(element, Tempuri)).FirstOrDefault()?.Value; }
        catch (System.Xml.XmlException) { return null; }
    }

    private static bool IsErrorCode(string s) => Regex.IsMatch(s.Trim(), @"^\d{3}$");

    /// <summary>Netcash allows no special characters in names.</summary>
    private static string Clean(string s) => Regex.Replace(s.Trim(), @"[^A-Za-z \-']", "").Trim();

    private static string Limit(string s, int max) => s.Length <= max ? s : s[..max];

    private static string El(string name, string value) => $"<{name}>{SecurityElement.Escape(value)}</{name}>";

    private static NetcashResult<T> MissingKey<T>() => NetcashResult<T>.Fail(NetcashCodes.NoActiveServiceKey,
        "This business's Netcash Risk Services key is not loaded, so no credit check was sent. Ask Netcash to activate risk reports and load the key.");
}

/// <summary>Stub bureau: a token now, a placeholder PDF on the next check. Nothing leaves the app.</summary>
public class StubCreditBureauClient : ICreditBureauClient
{
    private readonly ILogger<StubCreditBureauClient> _log;
    public StubCreditBureauClient(ILogger<StubCreditBureauClient> log) => _log = log;

    public Task<NetcashResult<string>> SubmitAsync(CreditCheckRequest request, CancellationToken ct = default)
    {
        _log.LogWarning("STUB credit check ({Bureau}) for {Reference}. No bureau was contacted.", request.Bureau, request.AccountReference);
        return Task.FromResult(NetcashResult<string>.Ok(StubNetcashDebiCheckService.StubFileToken()));
    }

    public Task<NetcashResult<byte[]>> FetchReportAsync(string fileToken, CancellationToken ct = default) =>
        Task.FromResult(NetcashResult<byte[]>.Ok(Encoding.ASCII.GetBytes(StubPdf)));

    /// <summary>A one-page PDF that says what it is.</summary>
    private const string StubPdf =
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
        "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n" +
        "4 0 obj<</Length 92>>stream\nBT /F1 18 Tf 60 760 Td (STUB credit report - no bureau was contacted.) Tj ET\nendstream endobj\n" +
        "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF";
}
