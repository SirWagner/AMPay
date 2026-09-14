using AMPay.Infrastructure.Netcash;

namespace AMPay.Tests;

/// <summary>
/// The NIF format fails quietly. A mis-ordered key record or a footer total that disagrees
/// with the body does not produce a transport error - it produces a load report full of line
/// errors, after the batch has been accepted. These tests pin the invariants the builder owns.
/// </summary>
public class NifFileBuilderTests
{
    private const string ServiceKey = "c74e1115-5499-4663-85fb-2a6bbbbfb9ef";
    private const string VendorKey = "24ade73c-98cf-47b3-99be-cc7b867b3080";

    private static NifFileBuilder Builder(string instruction = NifInstruction.TwoDay) =>
        new(ServiceKey, VendorKey, instruction, "My Test Batch", new DateTime(2026, 3, 31));

    [Fact]
    public void Header_carries_the_seven_fields_netcash_expects_in_order()
    {
        var file = Builder()
            .AddTransaction(t => t
                .Set(NifField.AccountReference, "0001")
                .SetAmount(NifField.Amount, 100m))
            .Build();

        var header = file.Split('\n')[0].Split('\t');

        Assert.Equal("H", header[0]);
        Assert.Equal(ServiceKey, header[1]);
        Assert.Equal("1", header[2]);
        Assert.Equal(NifInstruction.TwoDay, header[3]);
        Assert.Equal("My Test Batch", header[4]);
        Assert.Equal("20260331", header[5]);
        Assert.Equal(VendorKey, header[6]);
    }

    [Fact]
    public void Key_record_is_sorted_even_when_fields_are_supplied_out_of_order()
    {
        var file = Builder()
            .AddTransaction(t => t
                .SetAmount(NifField.Amount, 100m)          // 162
                .Set(NifField.AccountReference, "0001")     // 101
                .Set(NifField.BranchCode, "470010")         // 134
                .Set(NifField.AccountName, "T Mokoena"))    // 102
            .Build();

        var key = file.Split('\n')[1];

        Assert.Equal("K\t101\t102\t134\t162", key);
    }

    [Fact]
    public void Transaction_values_follow_the_key_record_order_not_the_call_order()
    {
        var file = Builder()
            .AddTransaction(t => t
                .SetAmount(NifField.Amount, 100m)
                .Set(NifField.AccountReference, "0001")
                .Set(NifField.AccountName, "T Mokoena"))
            .Build();

        var transaction = file.Split('\n')[2].Split('\t');

        Assert.Equal("T", transaction[0]);
        Assert.Equal("0001", transaction[1]);       // 101
        Assert.Equal("T Mokoena", transaction[2]);  // 102
        Assert.Equal("10000", transaction[3]);      // 162, in cents
    }

    [Fact]
    public void Rows_are_padded_so_every_transaction_matches_the_key_record()
    {
        // The second row omits a field the first one set.
        var file = Builder()
            .AddTransaction(t => t
                .Set(NifField.AccountReference, "0001")
                .Set(NifField.AccountName, "T Mokoena")
                .SetAmount(NifField.Amount, 100m))
            .AddTransaction(t => t
                .Set(NifField.AccountReference, "0002")
                .SetAmount(NifField.Amount, 50m))
            .Build();

        var lines = file.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var keyCount = lines[1].Split('\t').Length;

        Assert.Equal(keyCount, lines[2].Split('\t').Length);
        Assert.Equal(keyCount, lines[3].Split('\t').Length);

        // The gap is an empty column, not a missing one.
        Assert.Equal(string.Empty, lines[3].Split('\t')[2]);
    }

    [Fact]
    public void Footer_totals_are_computed_from_the_rows_actually_added()
    {
        var file = Builder()
            .AddTransaction(t => t.Set(NifField.AccountReference, "0001").SetAmount(NifField.Amount, 1450.50m))
            .AddTransaction(t => t.Set(NifField.AccountReference, "0002").SetAmount(NifField.Amount, 99.99m))
            .Build();

        var footer = file.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Split('\t');

        Assert.Equal("F", footer[0]);
        Assert.Equal("2", footer[1]);
        Assert.Equal("155049", footer[2]); // 145050 + 9999 cents
        Assert.Equal("9999", footer[3]);
    }

    [Fact]
    public void DebiCheck_batches_total_the_debicheck_amount_field_instead()
    {
        var file = Builder(NifInstruction.DebiCheckAuthentication)
            .WithAmountField(NifField.DebiCheckCollectionAmount)
            .AddTransaction(t => t
                .Set(NifField.AccountReference, "0001")
                .SetAmount(NifField.DebiCheckCollectionAmount, 250m))
            .Build();

        var footer = file.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Split('\t');

        Assert.Equal("25000", footer[2]);
    }

    [Theory]
    [InlineData(1450.50, 145050)]
    [InlineData(0.01, 1)]
    [InlineData(1000, 100000)]
    // Half a cent must round away from zero, not silently vanish from a collection total.
    [InlineData(0.005, 1)]
    [InlineData(10.555, 1056)]
    public void Amounts_convert_to_cents_without_losing_money(decimal rands, long expectedCents)
    {
        Assert.Equal(expectedCents, NifFileBuilder.ToCents(rands));
    }

    [Fact]
    public void Dates_are_always_ccyymmdd()
    {
        Assert.Equal("20260914", NifFileBuilder.FormatDate(new DateTime(2026, 9, 14)));
        Assert.Equal("20260101", NifFileBuilder.FormatDate(new DateTime(2026, 1, 1)));
    }

    [Fact]
    public void Records_are_tab_delimited_and_linefeed_terminated()
    {
        var file = Builder()
            .AddTransaction(t => t.Set(NifField.AccountReference, "0001").SetAmount(NifField.Amount, 10m))
            .Build();

        Assert.Contains('\t', file);
        Assert.DoesNotContain('\r', file);      // CRLF is not the documented terminator
        Assert.EndsWith("\n", file);
    }

    [Fact]
    public void A_file_with_no_transactions_is_refused_rather_than_sent()
    {
        var builder = Builder();
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void A_missing_service_key_is_refused_at_construction()
    {
        Assert.Throws<ArgumentException>(() =>
            new NifFileBuilder("", VendorKey, NifInstruction.TwoDay, "batch", DateTime.UtcNow));
    }
}

/// <summary>
/// Netcash reports only failures, so the absence of messages is the success signal. The trap
/// is "SUCCESSFUL WITH ERRORS", which is a partial failure that reads like a success.
/// </summary>
public class LoadReportParserTests
{
    [Fact]
    public void A_clean_report_is_fully_successful()
    {
        var raw = string.Join('\t', "###BEGIN", "My Test Batch", "SUCCESSFUL", "10:30 AM", "155049", "20260331")
                  + "\n"
                  + string.Join('\t', "###END", "10:31 AM") + "\n";

        var report = LoadReportParser.Parse(raw);

        Assert.True(report.IsFullySuccessful);
        Assert.False(report.HasErrors);
        Assert.Equal("My Test Batch", report.BatchName);
        Assert.Equal(new DateTime(2026, 3, 31), report.ActionDate);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void Successful_with_errors_is_not_treated_as_success()
    {
        var raw = string.Join('\t', "###BEGIN", "Batch", "SUCCESSFUL WITH ERRORS", "10:30 AM", "0", "20260331")
                  + "\n"
                  + string.Join('\t', "0002", "Line :3", "Invalid branch code") + "\n"
                  + string.Join('\t', "###END", "10:31 AM") + "\n";

        var report = LoadReportParser.Parse(raw);

        // Treating this as success would silently drop the rejected collections.
        Assert.False(report.IsFullySuccessful);
        Assert.True(report.HasErrors);

        var error = Assert.Single(report.Errors);
        Assert.Equal("0002", error.UniqueReference);
        Assert.Equal(3, error.LineNumber);
        Assert.Contains("Invalid branch code", error.Message);
    }

    [Fact]
    public void An_unsuccessful_report_has_errors_even_with_no_message_rows()
    {
        var raw = string.Join('\t', "###BEGIN", "Batch", "UNSUCCESSFUL", "10:30 AM", "0", "20260331")
                  + "\n"
                  + string.Join('\t', "###END", "10:31 AM") + "\n";

        var report = LoadReportParser.Parse(raw);

        Assert.False(report.IsFullySuccessful);
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void Several_line_errors_are_all_captured()
    {
        var raw = string.Join('\t', "###BEGIN", "Batch", "SUCCESSFUL WITH ERRORS", "10:30 AM", "0", "20260331")
                  + "\n"
                  + string.Join('\t', "0002", "Line :3", "Invalid branch code") + "\n"
                  + string.Join('\t', "0007", "Line :8", "Account number is not numeric") + "\n"
                  + string.Join('\t', "###END", "10:31 AM") + "\n";

        var report = LoadReportParser.Parse(raw);

        Assert.Equal(2, report.Errors.Count);
        Assert.Equal(new int?[] { 3, 8 }, report.Errors.Select(e => e.LineNumber).ToArray());
    }

    [Fact]
    public void Crlf_terminated_reports_parse_the_same_as_linefeed_ones()
    {
        var raw = string.Join('\t', "###BEGIN", "Batch", "SUCCESSFUL", "10:30 AM", "0", "20260331")
                  + "\r\n"
                  + string.Join('\t', "###END", "10:31 AM") + "\r\n";

        var report = LoadReportParser.Parse(raw);

        Assert.True(report.IsFullySuccessful);
        Assert.Equal("Batch", report.BatchName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_report_does_not_read_as_success(string? raw)
    {
        var report = LoadReportParser.Parse(raw);

        // Nothing came back, so nothing is confirmed. This must not look like a clean batch.
        Assert.False(report.IsFullySuccessful);
        Assert.Null(report.Result);
    }
}
