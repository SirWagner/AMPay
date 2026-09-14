namespace AMPay.Domain.Netcash;

/// <summary>
/// Uniform result for every Netcash call.
/// <para>
/// Netcash signals failure with a code in the response body rather than a transport error,
/// so a call can complete with HTTP 200 / no SOAP fault and still have failed. Wrapping every
/// call in this type stops that distinction getting lost at a call site.
/// </para>
/// </summary>
public class NetcashResult<T>
{
    public bool Success { get; init; }

    /// <summary>Netcash code as returned. "000" or "001" mean success depending on the service.</summary>
    public string Code { get; init; } = string.Empty;

    public string? Message { get; init; }
    public T? Data { get; init; }

    /// <summary>Raw response, retained for the audit trail. Scrub before persisting.</summary>
    public string? RawResponse { get; init; }

    public static NetcashResult<T> Ok(T data, string code = "000", string? raw = null) =>
        new() { Success = true, Code = code, Data = data, RawResponse = raw };

    public static NetcashResult<T> Fail(string code, string? message = null, string? raw = null) =>
        new() { Success = false, Code = code, Message = message ?? NetcashCodes.Describe(code), RawResponse = raw };
}

/// <summary>Netcash response codes, collected from the API documentation.</summary>
public static class NetcashCodes
{
    public const string Success = "000";
    public const string Authenticated = "001";
    public const string AuthenticationFailure = "100";
    public const string DateFormatError = "101";
    public const string ParameterError = "102";
    public const string NoActivePartner = "103";
    public const string NoActiveClient = "104";
    public const string NoActiveService = "105";
    public const string NoActiveServiceKey = "106";
    public const string GeneralServiceError = "200";
    public const string AccountLockedOut = "201";
    public const string Failed = "203";
    public const string NonRealTimeTemplate = "325";

    public const string BankResponseSuccess = "900000";
    public const string BankservAccepted = "ACCP";

    public static string Describe(string code) => code switch
    {
        Success => "Request submitted successfully.",
        Authenticated => "Authenticated.",
        AuthenticationFailure => "Authentication failure - the service key is invalid or not active for this service.",
        DateFormatError => "Date format error - dates must be CCYYMMDD.",
        ParameterError => "Parameter error - one or more input fields failed validation.",
        NoActivePartner => "No active partner found for this software vendor key.",
        NoActiveClient => "No active client found for this account number.",
        NoActiveService => "No active service found for this account number and service id.",
        NoActiveServiceKey => "No active service key for this account number, service id and key.",
        GeneralServiceError => "General Netcash service error - contact support@netcash.co.za.",
        AccountLockedOut => "Account locked out after three failed attempts in ten minutes.",
        Failed => "Failed - inspect the errors and warnings returned with the response.",
        NonRealTimeTemplate => "The mandate template is not configured for real-time DebiCheck, so TT1 cannot be used.",
        _ => $"Unmapped Netcash response code {code}."
    };
}
