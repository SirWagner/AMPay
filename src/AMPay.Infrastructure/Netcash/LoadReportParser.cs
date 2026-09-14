using System.Globalization;
using System.Text.RegularExpressions;

namespace AMPay.Infrastructure.Netcash;

/// <summary>A parsed Netcash load report.</summary>
public class LoadReport
{
    public string? BatchName { get; set; }

    /// <summary>SUCCESSFUL, SUCCESSFUL WITH ERRORS, or UNSUCCESSFUL.</summary>
    public string? Result { get; set; }

    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public string? BatchValue { get; set; }
    public DateTime? ActionDate { get; set; }

    public List<LoadReportError> Errors { get; set; } = new();

    /// <summary>
    /// True only for an unqualified SUCCESSFUL. "SUCCESSFUL WITH ERRORS" means some rows were
    /// rejected, so treating it as success would silently drop collections.
    /// </summary>
    public bool IsFullySuccessful =>
        string.Equals(Result, "SUCCESSFUL", StringComparison.OrdinalIgnoreCase) && Errors.Count == 0;

    public bool HasErrors => Errors.Count > 0
        || string.Equals(Result, "UNSUCCESSFUL", StringComparison.OrdinalIgnoreCase)
        || (Result?.Contains("WITH ERRORS", StringComparison.OrdinalIgnoreCase) ?? false);
}

public class LoadReportError
{
    public string? UniqueReference { get; set; }
    public int? LineNumber { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Parses the tab-delimited load report returned by RequestFileUploadReport.
/// <para>
/// Netcash reports errors only - a row that validated produces no output at all. So the
/// absence of messages is the success signal, and the result field must still be checked
/// because "SUCCESSFUL WITH ERRORS" is a real and easily-missed outcome.
/// </para>
/// </summary>
public static class LoadReportParser
{
    private const string BeginMarker = "###BEGIN";
    private const string EndMarker = "###END";

    private static readonly Regex LineNumberPattern =
        new(@"Line\s*:\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static LoadReport Parse(string? raw)
    {
        var report = new LoadReport();
        if (string.IsNullOrWhiteSpace(raw)) return report;

        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            var first = parts[0].Trim();

            if (first.StartsWith(BeginMarker, StringComparison.OrdinalIgnoreCase))
            {
                report.BatchName = Field(parts, 1);
                report.Result = Field(parts, 2);
                report.StartTime = Field(parts, 3);
                report.BatchValue = Field(parts, 4);

                var actionDate = Field(parts, 5);
                if (DateTime.TryParseExact(actionDate, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var parsed))
                    report.ActionDate = parsed;
            }
            else if (first.StartsWith(EndMarker, StringComparison.OrdinalIgnoreCase))
            {
                report.EndTime = Field(parts, 1);
            }
            else
            {
                // Anything between the markers is an error message record.
                var error = ParseErrorLine(parts);
                if (error is not null) report.Errors.Add(error);
            }
        }

        return report;
    }

    private static LoadReportError? ParseErrorLine(string[] parts)
    {
        var joined = string.Join(' ', parts).Trim();
        if (joined.Length == 0) return null;

        var error = new LoadReportError();

        // Reference, "Line :{n}", message - but Netcash is not consistent about how many
        // columns appear, so locate the line number by pattern rather than by position.
        var lineMatch = LineNumberPattern.Match(joined);
        if (lineMatch.Success &&
            int.TryParse(lineMatch.Groups[1].Value, out var lineNumber))
            error.LineNumber = lineNumber;

        if (parts.Length >= 3)
        {
            error.UniqueReference = Field(parts, 0);
            error.Message = string.Join(' ', parts.Skip(2)).Trim();
        }
        else
        {
            error.Message = joined;
        }

        if (string.IsNullOrWhiteSpace(error.Message)) error.Message = joined;
        return error;
    }

    private static string? Field(string[] parts, int index) =>
        index < parts.Length ? parts[index].Trim() : null;
}
