namespace AMPay.Portal.Services;

public class PortalOptions
{
    public const string SectionName = "Portal";

    /// <summary>
    /// The key AM-Pay presents on every API call. Empty disables the API. Outside
    /// Development the app refuses to start with an empty or development key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// No SMS provider yet: sign-in codes are shown on screen, under a TEST banner, instead of
    /// being sent. Never run a portal holding real client data with this on.
    /// </summary>
    public bool UseStubs { get; set; } = true;

    /// <summary>Where uploaded documents are kept. Outside wwwroot, always.</summary>
    public string DocumentRoot { get; set; } = "App_Data/portal-documents";

    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;
}

public static class ApplicantClaims
{
    public const string Scheme = "Applicant";
    public const string ApplicantId = "portal:applicant_id";
    public const string LenderId = "portal:lender_id";
    public const string Mobile = "portal:mobile";
}
