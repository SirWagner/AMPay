using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AMPay.Domain.Enums;
using AMPay.Domain.Netcash;
using AMPay.Domain.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AMPay.Infrastructure.Netcash;

/// <summary>
/// The real Netcash checks that run before a mandate: NIWS_Validation (bank account format and
/// check digit, ID number, bank lists) and NIWS_NIF.AVSRealtimeQuery (does the account exist and
/// belong to this person). Both endpoints are BasicHttpBinding, so this is plain SOAP 1.1.
/// <para>
/// Each half is switched on separately (<c>Netcash:Live:Validation</c>, <c>Netcash:Live:Avs</c>);
/// whichever is off answers from the stub. Keys come from the signed-in business through
/// <see cref="INetcashKeyProvider"/>, and nothing is sent without one. Keys are never logged.
/// </para>
/// </summary>
public class LiveNetcashValidationService : INetcashValidationService
{
    private const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Tempuri = "http://tempuri.org/";
    private const string Niws = "http://schemas.datacontract.org/2004/07/NC.DG.TMS.C.WCF.NIWS";
    private const string ArraysNs = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) });

    private readonly NetcashOptions _options;
    private readonly INetcashKeyProvider _keys;
    private readonly StubNetcashValidationService _stub;
    private readonly ILogger<LiveNetcashValidationService> _log;

    public LiveNetcashValidationService(
        IOptions<NetcashOptions> options, INetcashKeyProvider keys, StubNetcashValidationService stub,
        ILogger<LiveNetcashValidationService> log)
    {
        _options = options.Value;
        _keys = keys;
        _stub = stub;
        _log = log;
    }

    // ---------------------------------------------------------------- AVS (NIF)

    public async Task<NetcashResult<AvsResponse>> VerifyAccountAsync(string serviceKey, AvsRequest request, CancellationToken ct = default)
    {
        if (!_options.Live.Avs) return await _stub.VerifyAccountAsync(serviceKey, request, ct);

        var key = string.IsNullOrWhiteSpace(serviceKey) ? await _keys.KeyAsync(_options.AvsKeyService, ct) : serviceKey;
        if (key is null) return MissingKey<AvsResponse>(_options.AvsKeyService);

        var isId = !string.IsNullOrWhiteSpace(request.IdNumber);
        var body =
            $"<AVSRealtimeQuery xmlns=\"{Tempuri}\">" +
            El("ServiceKey", key) +
            El("AccountReference", request.AccountReference ?? "") +
            El("Extra1", "") + El("Extra2", "") + El("Extra3", "") +
            El("BankAccountNumber", request.AccountNumber) +
            El("BranchCode", request.BranchCode) +
            El("BankAccountType", AvsAccountType(request.AccountType)) +
            El("EnquiryName", "") +
            El("Initials", request.Initials ?? "") +
            El("IdNumber", isId ? request.IdNumber! : request.CompanyRegistrationNumber ?? "") +
            El("IsIdNumber", isId ? "true" : "false") +
            El("PhoneNumber", "") +
            El("Email", "") +
            "</AVSRealtimeQuery>";

        // AVS waits on the bank, like TT1: give it the long timeout.
        var (xml, error) = await PostAsync(_options.NifEndpoint, "http://tempuri.org/INIWS_NIF/AVSRealtimeQuery", body,
            _options.RealTimeTimeoutSeconds, ct);
        if (error is not null) return NetcashResult<AvsResponse>.Fail(NetcashCodes.GeneralServiceError, error);

        var result = ParseAvs(xml!);
        _log.LogInformation("Netcash AVS for branch {Branch}: {Outcome}.", request.BranchCode,
            result.Success ? (result.Data!.AnswerPending ? "answer pending" : $"account valid = {result.Data.AccountExists?.ToString() ?? "unknown"}") : result.Message);
        return result;
    }

    public static NetcashResult<AvsResponse> ParseAvs(string xml)
    {
        if (!TryParse(xml, out var doc, out var fault)) return NetcashResult<AvsResponse>.Fail(NetcashCodes.GeneralServiceError, fault);

        var r = doc!.Descendants(XName.Get("AVSRealtimeQueryResult", Tempuri)).FirstOrDefault();
        if (r is null) return NetcashResult<AvsResponse>.Fail(NetcashCodes.GeneralServiceError, "Netcash's answer had no AVS result in it.");

        XNamespace n = Niws;
        bool? M(string name) => r.Element(n + name)?.Value.Trim() switch
        {
            "Valid" => true,
            "Invalid" => false,
            _ => null
        };

        var messages = r.Element(n + "Messages")?.Elements(XName.Get("string", ArraysNs)).Select(e => e.Value.Trim())
            .Where(s => s.Length > 0).ToList() ?? new List<string>();

        var avs = new AvsResponse
        {
            AccountExists = M("BankAccountNumberValid"),
            AccountActive = M("AccountActive"),
            AccountOpenAtLeastThreeMonths = M("PeriodActive"),
            IdNumberMatches = M("IdNumberMatch"),
            InitialsMatch = M("InitialMatch"),
            SurnameMatches = M("LastNameMatch"),
            AccountAcceptsDebits = M("AcceptsDebits"),
            AccountAcceptsCredits = M("AcceptsCredits"),
            Reference = r.Element(n + "FileToken")?.Value.Trim() is { Length: > 0 } token ? token : null,
            AnswerPending = string.Equals(r.Element(n + "OfflineRequest")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            Messages = messages
        };

        // Nothing answered and only messages back: Netcash refused the query (bad key, bad input).
        var anything = avs.AccountExists is not null || avs.IdNumberMatches is not null || avs.AccountAcceptsDebits is not null;
        if (!anything && !avs.AnswerPending && messages.Count > 0)
            return new NetcashResult<AvsResponse>
            {
                Success = false,
                Code = NetcashCodes.Failed,
                Message = "Netcash did not run the check: " + string.Join("; ", messages),
                Data = avs
            };

        return NetcashResult<AvsResponse>.Ok(avs);
    }

    // ---------------------------------------------------------------- validation service

    public async Task<NetcashResult<bool>> ValidateBankAccountAsync(
        string accountNumber, string branchCode, BankAccountType accountType, CancellationToken ct = default)
    {
        if (!_options.Live.Validation) return await _stub.ValidateBankAccountAsync(accountNumber, branchCode, accountType, ct);

        var key = await _keys.KeyAsync(_options.ValidationKeyService, ct);
        if (key is null) return MissingKey<bool>(_options.ValidationKeyService);

        var code = await CallStringAsync("ValidateBankAccount",
            El("ServiceKey", key) + El("AccountNumber", accountNumber.Trim()) + El("BranchCode", branchCode.Trim()) +
            El("AccountType", ((int)accountType).ToString()), ct);
        if (code.Error is not null) return NetcashResult<bool>.Fail(NetcashCodes.GeneralServiceError, code.Error);

        return BankAccountResult(code.Value!);
    }

    /// <summary>Netcash's ValidateBankAccount answer codes.</summary>
    public static NetcashResult<bool> BankAccountResult(string code) => code.Trim() switch
    {
        "0" => NetcashResult<bool>.Ok(true, "0"),
        "1" => NetcashResult<bool>.Fail("1", "The branch code is not valid."),
        "2" => NetcashResult<bool>.Fail("2", "The account number failed the bank's check-digit test - check it for a typo."),
        "3" => NetcashResult<bool>.Fail("3", "That account type is not valid for this account."),
        "4" => NetcashResult<bool>.Fail("4", "The bank account details are incomplete or in the wrong format."),
        var other => Unexpected<bool>("ValidateBankAccount", other)
    };

    public async Task<NetcashResult<bool>> ValidateIdNumberAsync(string idNumber, CancellationToken ct = default)
    {
        if (!_options.Live.Validation) return await _stub.ValidateIdNumberAsync(idNumber, ct);

        // A typo is caught locally first: it costs Netcash nothing and the clerk gets an instant answer.
        if (!SaIdNumber.IsValid(idNumber))
            return NetcashResult<bool>.Fail(NetcashCodes.ParameterError, "South African ID number failed validation.");

        var key = await _keys.KeyAsync(_options.ValidationKeyService, ct);
        if (key is null) return MissingKey<bool>(_options.ValidationKeyService);

        var code = await CallStringAsync("ValidateId", El("ServiceKey", key) + El("IdNumber", idNumber.Trim()), ct);
        if (code.Error is not null) return NetcashResult<bool>.Fail(NetcashCodes.GeneralServiceError, code.Error);

        return IdResult(code.Value!);
    }

    public static NetcashResult<bool> IdResult(string code) => code.Trim() switch
    {
        "0" => NetcashResult<bool>.Ok(true, "0"),
        "1" => NetcashResult<bool>.Fail("1", "Netcash says this is not a valid South African ID number."),
        var other => Unexpected<bool>("ValidateId", other)
    };

    public async Task<NetcashResult<IReadOnlyList<BankInfo>>> GetBankListAsync(CancellationToken ct = default)
    {
        if (!_options.Live.Validation) return await _stub.GetBankListAsync(ct);

        var key = await _keys.KeyAsync(_options.ValidationKeyService, ct);
        if (key is null) return MissingKey<IReadOnlyList<BankInfo>>(_options.ValidationKeyService);

        var raw = await CallStringAsync("GetBankListWithDefaultBranchCode", El("ServiceKey", key), ct);
        if (raw.Error is not null) return NetcashResult<IReadOnlyList<BankInfo>>.Fail(NetcashCodes.GeneralServiceError, raw.Error);

        return BankList(raw.Value!, debiCheck: false, _log);
    }

    /// <summary>
    /// The DebiCheck banks, with each one's universal branch code taken from the full bank list
    /// (the DebiCheck call names the banks; the other carries the codes).
    /// </summary>
    public async Task<NetcashResult<IReadOnlyList<BankInfo>>> GetDebiCheckParticipatingBanksAsync(CancellationToken ct = default)
    {
        if (!_options.Live.Validation) return await _stub.GetDebiCheckParticipatingBanksAsync(ct);

        var live = await LiveDebiCheckBanksAsync(ct);
        if (live.Success) return live;

        // Never leave the capture screen without banks: fall back to the built-in list, and say so.
        _log.LogWarning("Netcash DebiCheck bank list unavailable ({Message}); using the built-in list.", live.Message);
        var fallback = await _stub.GetDebiCheckParticipatingBanksAsync(ct);
        return new NetcashResult<IReadOnlyList<BankInfo>>
        {
            Success = true, Code = live.Code, Data = fallback.Data,
            Message = $"Netcash's bank list is unavailable ({live.Message}) - showing AM-Pay's built-in list."
        };
    }

    private async Task<NetcashResult<IReadOnlyList<BankInfo>>> LiveDebiCheckBanksAsync(CancellationToken ct)
    {
        var key = await _keys.KeyAsync(_options.ValidationKeyService, ct);
        if (key is null) return MissingKey<IReadOnlyList<BankInfo>>(_options.ValidationKeyService);

        var raw = await CallStringAsync("GetDebiCheckParticipatingBanks", El("ServiceKey", key), ct);
        if (raw.Error is not null) return NetcashResult<IReadOnlyList<BankInfo>>.Fail(NetcashCodes.GeneralServiceError, raw.Error);

        var debiCheck = BankList(raw.Value!, debiCheck: true, _log);
        if (!debiCheck.Success || debiCheck.Data!.All(b => !string.IsNullOrEmpty(b.DefaultBranchCode))) return debiCheck;

        var all = await GetBankListAsync(ct);
        if (!all.Success) return debiCheck;

        var codes = all.Data!.GroupBy(b => Normalise(b.Name)).ToDictionary(g => g.Key, g => g.First().DefaultBranchCode);
        foreach (var bank in debiCheck.Data!.Where(b => string.IsNullOrEmpty(b.DefaultBranchCode)))
            bank.DefaultBranchCode = codes.GetValueOrDefault(Normalise(bank.Name));

        return debiCheck;
    }

    /// <summary>
    /// Reads Netcash's bank lists, which arrive as an XML document inside the SOAP string:
    /// <c>BankListResponse/BankList/BankName</c> for the DebiCheck banks, and
    /// <c>BankListResponse/BankList/Bank/(BankName, DefaultCode)</c> for all banks, each with
    /// <c>Errors/ErrorCode</c> (000 is success). Shapes confirmed against Netcash on 16 Oct 2026.
    /// </summary>
    public static NetcashResult<IReadOnlyList<BankInfo>> BankList(string raw, bool debiCheck, ILogger? log = null)
    {
        var text = raw.Trim();
        if (Regex.IsMatch(text, @"^\d{1,3}$"))
            return Unexpected<IReadOnlyList<BankInfo>>("bank list", text);

        XDocument doc;
        try { doc = XDocument.Parse(text); }
        catch (System.Xml.XmlException)
        {
            log?.LogWarning("Netcash bank list was not XML ({Length} characters).", text.Length);
            return NetcashResult<IReadOnlyList<BankInfo>>.Fail(NetcashCodes.GeneralServiceError, "Netcash's bank list came back in a shape AM-Pay could not read.");
        }

        var error = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ErrorCode")?.Value.Trim();
        if (!string.IsNullOrEmpty(error) && error != NetcashCodes.Success)
            return Unexpected<IReadOnlyList<BankInfo>>("bank list", error);

        var list = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "BankList");
        if (list is null)
            return NetcashResult<IReadOnlyList<BankInfo>>.Fail(NetcashCodes.GeneralServiceError, "Netcash's bank list was empty.");

        static string? Child(XElement e, string name) =>
            e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;

        var banks = list.Elements()
            .Select(e => e.Name.LocalName switch
            {
                "Bank" => Child(e, "BankName") is { } name
                    ? new BankInfo { Name = name, DefaultBranchCode = Child(e, "DefaultCode"), SupportsDebiCheck = debiCheck }
                    : null,
                "BankName" when e.Value.Trim().Length > 0 => new BankInfo { Name = e.Value.Trim(), SupportsDebiCheck = debiCheck },
                _ => null
            })
            .OfType<BankInfo>()
            .DistinctBy(b => Normalise(b.Name))
            .ToList();

        return banks.Count == 0
            ? NetcashResult<IReadOnlyList<BankInfo>>.Fail(NetcashCodes.GeneralServiceError, "Netcash's bank list was empty.")
            : NetcashResult<IReadOnlyList<BankInfo>>.Ok(banks);
    }

    // ---------------------------------------------------------------- plumbing

    private async Task<(string? Value, string? Error)> CallStringAsync(string operation, string parameters, CancellationToken ct)
    {
        var (xml, error) = await PostAsync(_options.ValidationEndpoint, $"http://tempuri.org/INIWS_Validation/{operation}",
            $"<{operation} xmlns=\"{Tempuri}\">{parameters}</{operation}>", _options.DefaultTimeoutSeconds, ct);
        if (error is not null) return (null, error);

        if (!TryParse(xml!, out var doc, out var fault)) return (null, fault);
        var value = doc!.Descendants(XName.Get($"{operation}Result", Tempuri)).FirstOrDefault()?.Value;

        // Short answers are result codes; long ones are bank lists. Neither carries a key.
        _log.LogInformation("Netcash {Operation} answered {Answer}.", operation,
            value is null ? "nothing" : value.Trim().Length <= 6 ? $"code {value.Trim()}" : $"{value.Length} characters, shape {Shape(value)}");

        return value is null ? (null, $"Netcash's answer to {operation} had no result in it.") : (value, null);
    }

    private async Task<(string? Xml, string? Error)> PostAsync(string endpoint, string action, string body, int timeoutSeconds, CancellationToken ct)
    {
        var envelope = $"<s:Envelope xmlns:s=\"{Soap11}\"><s:Body>{body}</s:Body></s:Envelope>";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(envelope, Encoding.UTF8) };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{action}\"");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await Http.SendAsync(request, cts.Token);
            var text = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode && !text.Contains("Envelope", StringComparison.Ordinal))
            {
                _log.LogWarning("Netcash {Action} returned HTTP {Status}.", action, (int)response.StatusCode);
                return (null, $"Netcash answered with HTTP {(int)response.StatusCode}. Try again shortly.");
            }
            return (text, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("Netcash {Action} unreachable: {Message}", action, ex.Message);
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

    /// <summary>The element names in an XML answer, in order and without values - safe to log.</summary>
    private static string Shape(string value)
    {
        try
        {
            return string.Join(">", XDocument.Parse(value).Descendants().Select(e => e.Name.LocalName).Distinct());
        }
        catch (System.Xml.XmlException)
        {
            return "not XML";
        }
    }

    private static string El(string name, string value) => $"<{name}>{SecurityElement.Escape(value)}</{name}>";

    /// <summary>The NIF enum names for an account type.</summary>
    public static string AvsAccountType(BankAccountType type) => type switch
    {
        BankAccountType.Savings => "Savings",
        _ => "Current"
    };

    private static string Normalise(string bank) => Regex.Replace(bank.ToUpperInvariant(), "[^A-Z0-9]", "");

    private static NetcashResult<T> MissingKey<T>(NetcashServiceId service) => NetcashResult<T>.Fail(NetcashCodes.NoActiveServiceKey,
        $"This business's Netcash {service} service key is not loaded, so the check was not sent. Load it on the Netcash service keys screen.");

    private static NetcashResult<T> Unexpected<T>(string what, string code) => code switch
    {
        NetcashCodes.AuthenticationFailure => NetcashResult<T>.Fail(code, $"Netcash refused the {what} check: the service key was not accepted (100)."),
        NetcashCodes.GeneralServiceError => NetcashResult<T>.Fail(code, $"Netcash had an error running the {what} check (200). Try again shortly."),
        _ => NetcashResult<T>.Fail(NetcashCodes.GeneralServiceError, $"Netcash answered the {what} check with an unexpected code: {code}.")
    };
}
