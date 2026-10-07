using AMPay.Domain.Contracts;
using AMPay.Domain.Enums;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using static AMPay.Domain.Contracts.ContractFormat;

namespace AMPay.Infrastructure.Contracts;

/// <summary>
/// Lays a contract snapshot out as an A4 PDF with MigraDoc (MIT licensed).
/// <para>
/// Every figure comes from the snapshot, never from the database, so a pack downloaded a
/// year from now prints exactly what the client was shown when it was issued.
/// </para>
/// </summary>
public class MigraDocContractRenderer : IContractRenderer
{
    private const string FontFamily = "Arial";

    private static readonly Color Ink = new(0x1F, 0x29, 0x37);
    private static readonly Color Muted = new(0x6B, 0x72, 0x80);
    private static readonly Color Rule = new(0xD1, 0xD5, 0xDB);
    private static readonly Color Band = new(0xF3, 0xF4, 0xF6);
    private static readonly Color Brand = new(0x1E, 0x36, 0x74); // --navy in ampay.css

    private static readonly object FontLock = new();

    public MigraDocContractRenderer()
    {
        // The font resolver is process-wide and may be set once only.
        lock (FontLock)
        {
            GlobalFontSettings.FontResolver ??= new ContractFontResolver();
        }
    }

    public byte[] RenderPdf(ContractSnapshot s, string snapshotHash, ContractSignatureRecord? signature = null)
    {
        ArgumentNullException.ThrowIfNull(s);

        var doc = new Document();
        doc.Info.Title = $"Credit agreement {s.Reference}";
        doc.Info.Author = s.Provider.RegisteredName;
        doc.Info.Subject = $"{s.Borrower.FullName} {s.Borrower.Surname}";
        DefineStyles(doc);

        var sec = doc.AddSection();
        sec.PageSetup.PageWidth = Unit.FromMillimeter(210);
        sec.PageSetup.PageHeight = Unit.FromMillimeter(297);
        sec.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        sec.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        sec.PageSetup.LeftMargin = Unit.FromCentimeter(1.8);
        sec.PageSetup.RightMargin = Unit.FromCentimeter(1.8);
        sec.PageSetup.FooterDistance = Unit.FromCentimeter(0.8);

        Footer(sec, s, snapshotHash);

        Heading(sec, s);
        Parties(sec, s);
        Quote(sec, s);
        Schedule(sec, s);
        Terms(sec, s, nameof(ContractTemplateKind.CreditAgreementTerms));

        if (s.Budget is not null)
        {
            Budget(sec, s.Budget);
            Terms(sec, s, nameof(ContractTemplateKind.BudgetAcknowledgement));
        }

        if (s.CreditLife is not null)
        {
            CreditLife(sec, s.CreditLife);
            Terms(sec, s, nameof(ContractTemplateKind.CreditLifeDisclosure));
        }

        Mandate(sec, s.Mandate);
        Terms(sec, s, nameof(ContractTemplateKind.DebitOrderAuthorisation));

        Signatures(sec, s, signature);

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();

        if (!s.TemplatesApproved)
            Watermark(renderer.PdfDocument);

        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ layout

    private static void DefineStyles(Document doc)
    {
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = FontFamily;
        normal.Font.Size = 8.5;
        normal.Font.Color = Ink;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(3);

        var h1 = doc.Styles[StyleNames.Heading1]!;
        h1.Font.Size = 15;
        h1.Font.Bold = true;
        h1.Font.Color = Brand;
        h1.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var h2 = doc.Styles[StyleNames.Heading2]!;
        h2.Font.Size = 10.5;
        h2.Font.Bold = true;
        h2.Font.Color = Brand;
        h2.ParagraphFormat.SpaceBefore = Unit.FromPoint(12);
        h2.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);
        h2.ParagraphFormat.KeepWithNext = true;
        h2.ParagraphFormat.Borders.Bottom.Width = 0.6;
        h2.ParagraphFormat.Borders.Bottom.Color = Rule;

        var small = doc.Styles.AddStyle("Small", StyleNames.Normal);
        small.Font.Size = 7;
        small.Font.Color = Muted;
    }

    private static void Footer(Section sec, ContractSnapshot s, string hash)
    {
        var p = sec.Footers.Primary.AddParagraph();
        p.Style = "Small";
        p.AddText($"{s.Provider.RegisteredName}   |   Agreement {s.Reference}   |   Fingerprint {ShortHash(hash)}   |   Page ");
        p.AddPageField();
        p.AddText(" of ");
        p.AddNumPagesField();
    }

    private static void Heading(Section sec, ContractSnapshot s)
    {
        sec.AddParagraph(s.Provider.TradingName ?? s.Provider.RegisteredName, StyleNames.Heading1);

        var sub = sec.AddParagraph();
        sub.Format.Font.Size = 11;
        sub.Format.Font.Bold = true;
        sub.AddText("Pre-agreement statement, quote and short-term credit agreement");

        var meta = sec.AddParagraph();
        meta.Style = "Small";
        meta.AddText($"Agreement {s.Reference}   |   Issued {SastTime(s.IssuedUtc)}   |   Package {s.Quote.PackageName}"
                     + (s.Quote.Customised ? " (customised rates)" : ""));

        if (!s.TemplatesApproved)
        {
            var draft = sec.AddParagraph();
            draft.Format.Font.Bold = true;
            draft.Format.Font.Color = new Color(0xB9, 0x1C, 0x1C);
            draft.Format.SpaceBefore = Unit.FromPoint(4);
            draft.AddText("DRAFT - the wording in this document has not yet been approved by the credit provider's attorney. Not for signature.");
        }
    }

    private static void Parties(Section sec, ContractSnapshot s)
    {
        sec.AddParagraph("1. The parties", StyleNames.Heading2);

        var p = s.Provider;
        var b = s.Borrower;

        var t = NewTable(sec, 4.0, 4.7, 4.0, 4.7);
        Pair(t, "Credit provider", p.RegisteredName, "Borrower", $"{b.FullName} {b.Surname}");
        Pair(t, "Trading as", p.TradingName, "ID number", b.IdNumber);
        Pair(t, "Registration number", p.RegistrationNumber, "Client number", b.ClientNumber);
        Pair(t, "NCR registration", p.NcrNumber, "Employer", b.Employer);
        Pair(t, "VAT number", p.VatNumber, "Residential address", b.ResidentialAddress);
        Pair(t, "Physical address", p.PhysicalAddress, "Postal address", b.PostalAddress ?? b.ResidentialAddress);
        Pair(t, "Postal address", p.PostalAddress, "Work address", b.WorkAddress);
        Pair(t, "Telephone", p.ContactNumber, "Cell number", b.Mobile);
        Pair(t, "Email", p.ContactEmail, "Email", b.Email);
    }

    private static void Quote(Section sec, ContractSnapshot s)
    {
        var q = s.Quote;
        sec.AddParagraph("2. Quote and cost of credit", StyleNames.Heading2);

        var intro = sec.AddParagraph(
            $"Quote dated {Date(q.QuoteDate)}. This quote is binding on the credit provider for five business days.");
        intro.Style = "Small";

        var t = NewTable(sec, 1.2, 10.8, 5.4);
        t.Columns[2].Format.Alignment = ParagraphAlignment.Right;

        Line(t, "(a)", "Loan amount paid to you", Money(q.LoanAmount));
        Line(t, "(b)", "Optional family funeral insurance paid on your behalf", Money(q.FuneralInsurance));
        Line(t, "(c)", "Optional payment to a third party on your behalf", Money(q.ThirdPartyPayment));
        Line(t, "(d)", "Total loan amount (a) + (b) + (c)", Money(q.TotalLoanAmount), bold: true);
        Line(t, "(f.1)", $"Credit life insurance premium", Money(q.CreditLife));
        Line(t, "(f.2)", "Initiation fee", Money(q.InitiationFee));
        Line(t, "(f.3)", "Service fees", Money(q.ServiceFees));
        Line(t, "(f.4)", "Interest", Money(q.Interest));
        Line(t, "(f.5)", "VAT", Money(q.Vat));
        Line(t, "(e)", "Total cost of credit (f.1) to (f.5)", Money(q.TotalCostOfCredit), bold: true);
        Line(t, "(g)", "Total amount repayable (d) + (e)", Money(q.TotalRepayable), bold: true, shade: true);
        Line(t, "(h)", "Interest rate",
            $"{Percent(q.MonthlyInterestRate)} a month ({Percent(q.AnnualInterestRate)} a year)");
        Line(t, "(i)", "Interest rate on arrears", $"{Percent(q.PenaltyInterestRate)} a month");
        Line(t, "(j)", "Number of instalments", q.NumberOfInstalments.ToString());
        Line(t, "", "Instalment", Money(q.InstalmentAmount));

        if (q.FinalInstalmentAmount != 0 && q.FinalInstalmentAmount != q.InstalmentAmount)
            Line(t, "", "Final instalment", Money(q.FinalInstalmentAmount));

        Line(t, "", "Frequency and method", $"{q.Frequency}, {q.PaymentMethod}");
        Line(t, "", "First and final payment", $"{Date(q.FirstPaymentDate)} to {Date(q.FinalPaymentDate)}");
        Line(t, "(l)", "Credit cost multiple (g) / (d)", q.CreditCostMultiple.ToString("N2"));
    }

    private static void Schedule(Section sec, ContractSnapshot s)
    {
        sec.AddParagraph("3. Payment schedule", StyleNames.Heading2);

        var t = NewTable(sec, 1.0, 2.4, 2.4, 2.4, 2.4, 2.2, 2.2, 2.4);
        for (var c = 2; c < 8; c++) t.Columns[c].Format.Alignment = ParagraphAlignment.Right;

        var head = t.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = Band;
        head.Format.Font.Bold = true;
        string[] titles = { "No.", "Due date", "Instalment", "Interest", "Capital", "Service fee", "Credit life", "Balance" };
        for (var c = 0; c < titles.Length; c++) head.Cells[c].AddParagraph(titles[c]);

        foreach (var l in s.Schedule)
        {
            var r = t.AddRow();
            r.Cells[0].AddParagraph(l.Number.ToString());
            r.Cells[1].AddParagraph(Date(l.DueDate));
            r.Cells[2].AddParagraph(l.Instalment.ToString("N2"));
            r.Cells[3].AddParagraph(l.Interest.ToString("N2"));
            r.Cells[4].AddParagraph(l.Capital.ToString("N2"));
            r.Cells[5].AddParagraph(l.ServiceFee.ToString("N2"));
            r.Cells[6].AddParagraph(l.CreditLife.ToString("N2"));
            r.Cells[7].AddParagraph(l.ClosingBalance.ToString("N2"));
        }

        var total = t.AddRow();
        total.Format.Font.Bold = true;
        total.Cells[0].MergeRight = 1;
        total.Cells[0].AddParagraph("Total");
        total.Cells[2].AddParagraph(s.Schedule.Sum(l => l.Instalment).ToString("N2"));
        total.Cells[3].AddParagraph(s.Schedule.Sum(l => l.Interest).ToString("N2"));
        total.Cells[4].AddParagraph(s.Schedule.Sum(l => l.Capital).ToString("N2"));
        total.Cells[5].AddParagraph(s.Schedule.Sum(l => l.ServiceFee).ToString("N2"));
        total.Cells[6].AddParagraph(s.Schedule.Sum(l => l.CreditLife).ToString("N2"));
    }

    private static void Budget(Section sec, BudgetDetails b)
    {
        sec.AddParagraph("Budget", StyleNames.Heading2);

        var t = NewTable(sec, 12.0, 5.4);
        t.Columns[1].Format.Alignment = ParagraphAlignment.Right;

        Line(t, "Net salary", Money(b.NetSalary));
        if (b.OtherIncome > 0) Line(t, "Other income", Money(b.OtherIncome));

        foreach (var l in b.Lines)
            Line(t, l.Kind == "Instalment" ? $"{l.Description} (credit instalment)" : l.Description, Money(l.Amount));

        Line(t, "Declared living expenses", Money(b.DeclaredLivingExpenses), bold: true);
        Line(t, "Minimum living expenses under the regulations", Money(b.StatutoryMinimum));
        Line(t, "Existing credit instalments", Money(b.DebtInstalments), bold: true);
        Line(t, "Net of net: income left after expenses and instalments", Money(b.NetOfNet), bold: true, shade: true);
    }

    private static void CreditLife(Section sec, CreditLifeDetails c)
    {
        sec.AddParagraph("Credit life cover", StyleNames.Heading2);

        var t = NewTable(sec, 6.0, 11.4);
        Line(t, "Insurer (underwriter)", c.Underwriter ?? "To be confirmed by the credit provider");
        Line(t, "Administrator", c.Administrator ?? "To be confirmed by the credit provider");
        Line(t, "Premium rate", $"{Percent(c.MonthlyRate)} a month of the outstanding balance");
        Line(t, "Total premium over the term", Money(c.TotalPremium));
        Line(t, "Sum assured", $"The outstanding balance, up to {Money(c.SumAssured)}");
        Line(t, "Cover period", $"{Date(c.CommencementDate)} to {Date(c.TerminationDate)}");
    }

    private static void Mandate(Section sec, MandateDetails m)
    {
        sec.AddParagraph("DebiCheck debit order mandate", StyleNames.Heading2);

        var t = NewTable(sec, 6.0, 11.4);
        Line(t, "Account holder", m.AccountHolder ?? "Not captured");
        Line(t, "Bank", m.BankName ?? "Not captured");
        Line(t, "Branch code", m.BranchCode ?? "");
        Line(t, "Account number", m.MaskedAccountNumber ?? "Not captured");
        Line(t, "Account type", m.AccountType ?? "");
        Line(t, "Instalment", Money(m.InstalmentAmount));
        Line(t, "Number of instalments", m.NumberOfInstalments.ToString());
        Line(t, "Total to be collected", Money(m.TotalAmount));
        Line(t, "Frequency", m.Frequency);
        Line(t, "Collection day", m.CollectionDay ?? "Salary day");
        Line(t, "First collection", m.FirstCollectionDate is { } d ? Date(d) : "As per the payment schedule");
        Line(t, "Tracking", $"Up to {m.TrackingDays} days");
        Line(t, "Agreement reference", m.AgreementReference);
        Line(t, "Shown on your bank statement as", m.StatementReference, bold: true);

        var note = sec.AddParagraph(
            "The full account number is held by the credit provider and is shown masked here so that this document can be sent by link safely.");
        note.Style = "Small";
    }

    private static void Terms(Section sec, ContractSnapshot s, string kind)
    {
        var section = s.Terms.FirstOrDefault(t => t.Kind == kind);
        if (section is null) return;

        sec.AddParagraph(section.Title, StyleNames.Heading2);
        foreach (var para in Paragraphs(section.Body))
        {
            var p = sec.AddParagraph(para);
            p.Format.Alignment = ParagraphAlignment.Justify;
        }
    }

    private static void Signatures(Section sec, ContractSnapshot s, ContractSignatureRecord? sig)
    {
        sec.AddParagraph("Signature", StyleNames.Heading2);

        if (sig is null)
        {
            sec.AddParagraph(
                "By signing, the borrower confirms that they have read and understood the quote, the payment schedule, " +
                "the terms and conditions, the budget and the debit order mandate in this document, and accepts them.");

            sec.AddParagraph(
                "This agreement may be signed online with a one-time PIN sent to the borrower's cell number, " +
                "or printed, signed and returned to the credit provider.").Style = "Small";

            var t = NewTable(sec, 8.7, 8.7);
            t.Borders.Visible = false;
            var r = t.AddRow();
            r.TopPadding = Unit.FromCentimeter(1.2);
            r.Cells[0].AddParagraph("______________________________________");
            r.Cells[1].AddParagraph("______________________________________");
            var r2 = t.AddRow();
            r2.Cells[0].AddParagraph($"Borrower: {s.Borrower.FullName} {s.Borrower.Surname}");
            r2.Cells[1].AddParagraph($"For the credit provider: {s.Provider.RegisteredName}");
            var r3 = t.AddRow();
            r3.Cells[0].AddParagraph("Date and place: ____________________");
            r3.Cells[1].AddParagraph("Date and place: ____________________");
            return;
        }

        var how = sig.Method switch
        {
            SignatureMethod.OnlineOtp => "Signed online with a one-time PIN sent to the borrower's cell number",
            SignatureMethod.UploadedSignedCopy => "Signed on paper; the signed copy is held on file",
            _ => "Signed"
        };

        var tbl = NewTable(sec, 6.0, 11.4);
        Line(tbl, "Method", how, bold: true);
        Line(tbl, "Signed by", sig.SignedName);
        if (!string.IsNullOrWhiteSpace(sig.SignedIdNumber)) Line(tbl, "ID number given", sig.SignedIdNumber);
        if (!string.IsNullOrWhiteSpace(sig.MaskedMobile)) Line(tbl, "PIN sent to", sig.MaskedMobile);
        Line(tbl, "Date and time", SastTime(sig.SignedUtc));
        if (!string.IsNullOrWhiteSpace(sig.IpAddress)) Line(tbl, "From IP address", sig.IpAddress);
        if (!string.IsNullOrWhiteSpace(sig.RecordedBy)) Line(tbl, "Recorded by", sig.RecordedBy);
        Line(tbl, "Agreement reference", s.Reference);

        sec.AddParagraph(
            "The fingerprint in the footer is a SHA-256 digest of the agreement exactly as issued. " +
            "The credit provider can prove that this document has not changed since it was signed by recomputing it.")
            .Style = "Small";
    }

    // ------------------------------------------------------------------ table helpers

    private static Table NewTable(Section sec, params double[] widthsCm)
    {
        var t = sec.AddTable();
        t.Borders.Width = 0.4;
        t.Borders.Color = Rule;
        t.Rows.LeftIndent = 0;
        t.TopPadding = Unit.FromPoint(1.5);
        t.BottomPadding = Unit.FromPoint(1.5);
        foreach (var w in widthsCm) t.AddColumn(Unit.FromCentimeter(w));
        return t;
    }

    private static void Pair(Table t, string l1, string? v1, string l2, string? v2)
    {
        var r = t.AddRow();
        Label(r.Cells[0], l1);
        r.Cells[1].AddParagraph(v1 ?? "");
        Label(r.Cells[2], l2);
        r.Cells[3].AddParagraph(v2 ?? "");
    }

    private static void Line(Table t, string label, string value, bool bold = false, bool shade = false)
    {
        var r = t.AddRow();
        if (shade) r.Shading.Color = Band;
        r.Format.Font.Bold = bold;
        r.Cells[0].AddParagraph(label);
        r.Cells[1].AddParagraph(value);
    }

    private static void Line(Table t, string code, string label, string value, bool bold = false, bool shade = false)
    {
        var r = t.AddRow();
        if (shade) r.Shading.Color = Band;
        r.Format.Font.Bold = bold;
        r.Cells[0].AddParagraph(code).Format.Font.Color = Muted;
        r.Cells[1].AddParagraph(label);
        r.Cells[2].AddParagraph(value);
    }

    private static void Label(Cell c, string text)
    {
        c.Shading.Color = Band;
        c.AddParagraph(text).Format.Font.Color = Muted;
    }

    /// <summary>A pale diagonal DRAFT across every page, drawn over the content so it cannot be hidden by a shaded cell.</summary>
    private static void Watermark(PdfSharp.Pdf.PdfDocument pdf)
    {
        var font = new XFont(FontFamily, 110, XFontStyleEx.Bold);
        var brush = new XSolidBrush(XColor.FromArgb(38, 185, 28, 28));

        foreach (var page in pdf.Pages)
        {
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            gfx.TranslateTransform(page.Width.Point / 2, page.Height.Point / 2);
            gfx.RotateTransform(-50);
            gfx.DrawString("DRAFT", font, brush, 0, 0, XStringFormats.Center);
        }
    }
}
