using System.Net;
using System.Net.Http.Json;
using AMPay.Domain.Entities;
using AMPay.Domain.Portal;
using Microsoft.Extensions.Options;

namespace AMPay.Web.Services;

public class PortalOptions
{
    public const string SectionName = "Portal";

    /// <summary>The portal's public address, e.g. https://apply.ampay.co.za. Links are built from it.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The key AM-Pay presents to the portal API. From Key Vault or user-secrets, never appsettings.</summary>
    public string? ApiKey { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>A refusal or failure from the portal, worded for the person on screen.</summary>
public class PortalUnavailableException : Exception
{
    public PortalUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>What AM-Pay asks of the portal. An interface so the importer can be tested without one.</summary>
public interface IPortalApi
{
    bool IsConfigured { get; }
    string? LinkFor(string? code);
    Task PutLenderAsync(string code, PortalLenderSync body, CancellationToken ct = default);
    Task<IReadOnlyList<PortalApplicationSummary>> ApplicationsAsync(Guid tenantId, CancellationToken ct = default);
    Task<PortalApplicationDetail?> ApplicationAsync(Guid tenantId, Guid id, CancellationToken ct = default);
    Task<byte[]?> DocumentAsync(Guid tenantId, Guid applicationId, Guid documentId, CancellationToken ct = default);
    Task MarkPickedUpAsync(Guid tenantId, Guid id, PortalPickedUp body, CancellationToken ct = default);
    Task<IReadOnlyList<PortalCallback>> CallbacksAsync(Guid tenantId, CancellationToken ct = default);
    Task MarkCallbackHandledAsync(Guid tenantId, Guid id, CancellationToken ct = default);
}

/// <summary>
/// AM-Pay's side of the portal API. Every call starts here - the portal never calls AM-Pay.
/// </summary>
public class PortalClient : IPortalApi
{
    private readonly HttpClient _http;
    private readonly PortalOptions _options;

    public PortalClient(HttpClient http, IOptions<PortalOptions> options)
    {
        _http = http;
        _options = options.Value;

        if (_options.IsConfigured)
        {
            _http.BaseAddress = new Uri(_options.BaseUrl!.TrimEnd('/') + "/");
            _http.DefaultRequestHeaders.Add(PortalApi.ApiKeyHeader, _options.ApiKey);
        }

        // The portal's database pauses when idle; the first call after that can take a while.
        _http.Timeout = TimeSpan.FromSeconds(90);
    }

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>The public link for a lender code, or null when the portal address is not set.</summary>
    public string? LinkFor(string? code) =>
        string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(_options.BaseUrl)
            ? null
            : $"{_options.BaseUrl!.TrimEnd('/')}/a/{code}";

    public static PortalLenderSync SyncFor(Tenant t, IEnumerable<CreditPackage> packages) => new(
        t.Id, t.Name, t.TradingName, t.NcrNumber, t.ContactNumber, t.ContactEmail, t.AdvisorWhatsApp,
        IsActive: t.Status is not (Domain.Enums.TenantStatus.Suspended or Domain.Enums.TenantStatus.Closed),
        Packages: packages
            .Where(p => p.IsActive)
            .OrderBy(p => p.Tier)
            .Select(p => new PortalPackage(p.Name, p.Tier, p.MonthlyInterestRate, p.MonthlyServiceFee, p.InitiationFeeRate,
                p.CreditLifeRate, p.MinLoanAmount, p.MaxLoanAmount, p.MinTermMonths, p.MaxTermMonths))
            .ToList());

    public Task PutLenderAsync(string code, PortalLenderSync body, CancellationToken ct = default) =>
        SendAsync(() => _http.PutAsJsonAsync($"{PortalApi.LendersPath}/{code}", body, ct));

    public async Task<IReadOnlyList<PortalApplicationSummary>> ApplicationsAsync(Guid tenantId, CancellationToken ct = default) =>
        await GetAsync<List<PortalApplicationSummary>>($"{PortalApi.ApplicationsPath}?tenantId={tenantId}", ct) ?? new();

    public Task<PortalApplicationDetail?> ApplicationAsync(Guid tenantId, Guid id, CancellationToken ct = default) =>
        GetAsync<PortalApplicationDetail>($"{PortalApi.ApplicationsPath}/{id}?tenantId={tenantId}", ct);

    /// <summary>The document's bytes. Small enough (10 MB cap) to hold in memory for the copy into AM-Pay.</summary>
    public async Task<byte[]?> DocumentAsync(Guid tenantId, Guid applicationId, Guid documentId, CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            using var response = await _http.GetAsync($"{PortalApi.ApplicationsPath}/{applicationId}/documents/{documentId}?tenantId={tenantId}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            await ThrowIfFailedAsync(response);
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (HttpRequestException ex) { throw Unreachable(ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw Unreachable(ex); }
    }

    public Task MarkPickedUpAsync(Guid tenantId, Guid id, PortalPickedUp body, CancellationToken ct = default) =>
        SendAsync(() => _http.PostAsJsonAsync($"{PortalApi.ApplicationsPath}/{id}/picked-up?tenantId={tenantId}", body, ct));

    public async Task<IReadOnlyList<PortalCallback>> CallbacksAsync(Guid tenantId, CancellationToken ct = default) =>
        await GetAsync<List<PortalCallback>>($"{PortalApi.CallbacksPath}?tenantId={tenantId}&open=true", ct) ?? new();

    public Task MarkCallbackHandledAsync(Guid tenantId, Guid id, CancellationToken ct = default) =>
        SendAsync(() => _http.PostAsync($"{PortalApi.CallbacksPath}/{id}/handled?tenantId={tenantId}", null, ct));

    // ---------------------------------------------------------------- plumbing

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        EnsureConfigured();
        try
        {
            using var response = await _http.GetAsync(path, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return default;
            await ThrowIfFailedAsync(response);
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (HttpRequestException ex) { throw Unreachable(ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw Unreachable(ex); }
    }

    private async Task SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        EnsureConfigured();
        try
        {
            using var response = await send();
            await ThrowIfFailedAsync(response);
        }
        catch (HttpRequestException ex) { throw Unreachable(ex); }
        catch (TaskCanceledException ex) { throw Unreachable(ex); }
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var detail = (await response.Content.ReadAsStringAsync()).Trim().Trim('"');
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new PortalUnavailableException(
                "The self-service portal rejected AM-Pay's key. Check that Portal:ApiKey is the same in both apps."),
            HttpStatusCode.Conflict => new PortalUnavailableException(string.IsNullOrEmpty(detail) ? "The portal refused the change." : detail),
            _ => new PortalUnavailableException($"The self-service portal returned an error ({(int)response.StatusCode}). Try again shortly.")
        };
    }

    private void EnsureConfigured()
    {
        if (!_options.IsConfigured)
            throw new PortalUnavailableException("The self-service portal is not configured. Set Portal:BaseUrl and Portal:ApiKey.");
    }

    private static PortalUnavailableException Unreachable(Exception ex) =>
        new("The self-service portal could not be reached. It may be starting up - try again in a minute.", ex);
}
