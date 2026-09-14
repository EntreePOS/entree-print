namespace EntreePrintTray;

internal static class PrinterIconFactory
{
    public static Icon CreatePrinterIcon()
    {
        using var stream = typeof(PrinterIconFactory).Assembly.GetManifestResourceStream("EntreePrint.Icon")
            ?? throw new InvalidOperationException("The packaged printer icon is missing.");
        using var icon = new Icon(stream, SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }
}
