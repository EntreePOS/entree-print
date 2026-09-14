namespace EntreePrint.Configuration;

internal static class SecurityConfiguration
{
    public static string NormalizeOrigins(string value)
    {
        if (value.Trim() == "*") return "*";
        var origins = new List<string>();
        foreach (var entry in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.AbsolutePath != "/" || entry.Contains('*') || entry.Contains('\\'))
                throw new ArgumentException("Enter your POS website address, for example https://pos.example.com or http://192.168.1.20:8080. Remove the page name, such as /orders. Separate multiple addresses with commas, or enter * by itself to allow any website.");
            origins.Add(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant());
        }
        return string.Join(",", origins.Distinct(StringComparer.Ordinal));
    }
}
