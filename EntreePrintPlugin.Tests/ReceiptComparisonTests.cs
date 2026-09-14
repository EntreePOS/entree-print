using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class ReceiptComparisonTests
{
    private static ReceiptTextLayout Layout() => new(272, 100,
        [new("Receipt café", 0, 20, 120, 16, "Arial", false, false, "#000")],
        [new(0, 30, 20, 20, "#000", 203)], [new(30, 20)]);
    private static readonly PrinterLayoutSettings Driver = new(576, 1600, 203, 203, 640, 1700, 10, 10);
    private static string? Compare(ReceiptTextLayout? layout = null, PrinterLayoutSettings? driver = null,
        string driverName = "Receipt driver", string? font = "font-a") =>
        ReceiptComparison.Create(layout ?? Layout(), driverName, driver ?? Driver, CancellationToken.None, _ => font);

    [Fact]
    public void IdenticalSnapshotsHaveStableOpaqueIds()
    {
        Assert.Matches("^layout:[a-f0-9]{64}$", Compare()!);
        Assert.Equal(Compare(), Compare(Layout() with { }));
        Assert.NotEqual(Compare(), Compare(driverName: "Other driver"));
        Assert.NotEqual(Compare(), Compare(font: "font-b"));
        Assert.Null(Compare(font: null));
    }

    [Fact]
    public void EveryDriverMeasurementParticipates()
    {
        PrinterLayoutSettings[] changed = [Driver with { PrintableWidthDots = 575 }, Driver with { PrintableHeightDots = 1599 },
            Driver with { DpiX = 300 }, Driver with { DpiY = 300 }, Driver with { PaperWidthDots = 639 },
            Driver with { PaperHeightDots = 1699 }, Driver with { OffsetXDots = 11 }, Driver with { OffsetYDots = 11 }];
        foreach (var driver in changed) Assert.NotEqual(Compare(), Compare(driver: driver));
    }

    [Fact]
    public void TextStyleGeometryCodesAndPaginationParticipate()
    {
        var layout = Layout();
        var run = layout.Text[0];
        ReceiptTextRun[] changed = [run with { Text = "Different" }, run with { X = 1 }, run with { Baseline = 21 },
            run with { Width = 121 }, run with { Size = 17 }, run with { Family = "Segoe UI" },
            run with { Bold = true }, run with { Italic = true }, run with { Color = "#111" }];
        foreach (var text in changed) Assert.NotEqual(Compare(), Compare(layout with { Text = [text] }));
        Assert.NotEqual(Compare(), Compare(layout with { Width = 271 }));
        Assert.NotEqual(Compare(), Compare(layout with { Height = 101 }));
        Assert.NotEqual(Compare(), Compare(layout with { Rectangles = [layout.Rectangles[0] with { X = 1 }] }));
        Assert.NotEqual(Compare(), Compare(layout with { Rectangles = [layout.Rectangles[0] with { DotDpi = 300 }] }));
        Assert.NotEqual(Compare(), Compare(layout with { KeepTogether = [new(30, 21)] }));
    }

    [Fact]
    public void RepeatedFontIsReadOnceWithAllItsText()
    {
        var layout = Layout();
        layout = layout with { Text = [layout.Text[0], layout.Text[0] with { Text = "Second", Baseline = 40 }] };
        var calls = new List<ReceiptTextRun>();
        Assert.NotNull(ReceiptComparison.Create(layout, "driver", Driver, default, font => { calls.Add(font); return "digest"; }));
        Assert.Equal("Receipt caféSecond", Assert.Single(calls).Text);
    }

    [Fact]
    public void ExcessiveFontsAndCancellationDoNotProduceEvidence()
    {
        var layout = Layout();
        Assert.Null(ReceiptComparison.Create(layout with {
            Text = Enumerable.Range(1, 65).Select(size => layout.Text[0] with { Size = size }).ToArray()
        }, "driver", Driver, default, _ => throw new Exception("Must reject before reading fonts")));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ReceiptComparison.Create(layout, "driver", Driver, cancellation.Token));
        Assert.Throws<ArgumentException>(() => Compare(layout with { Width = -1 }));
    }

    [Fact]
    public void InstalledTrueTypeFontIsReadWithoutPrinting()
    {
        var first = ReceiptComparison.Create(Layout(), "driver", Driver, default);
        Assert.Matches("^layout:[a-f0-9]{64}$", first!);
        Assert.Equal(first, ReceiptComparison.Create(Layout(), "driver", Driver, default));
        var chinese = WindowsTextPrinter.ResolveFonts(Layout() with { Text = [Layout().Text[0] with { Text = "欢迎 café" }] });
        Assert.NotNull(ReceiptComparison.Create(chinese, "driver", Driver, default));
    }

    [Theory]
    [InlineData("Font-That-Is-Not-Installed-6fb2", "Receipt")]
    [InlineData("Arial", "欢迎")]
    [InlineData("Arial", "😀")]
    public void SubstitutionOrUnverifiedGlyphCoverageCannotClaimComparison(string family, string text)
    {
        var layout = Layout();
        Assert.Null(ReceiptComparison.Create(layout with { Text = [layout.Text[0] with { Family = family, Text = text }] },
            "driver", Driver, default));
    }
}
