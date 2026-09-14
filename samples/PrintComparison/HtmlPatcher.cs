namespace EntreePrintPlugin.Services;

// Historical image comparison template wrapper, outside the production service.
public static class HtmlPatcher
{
    public static string Patch(string html, string style)
    {
        var css = style.Trim();
        var styleTag = css.Length == 0 ? "" : $"<style>{css}</style>";
        var content = html.Trim();

        if (!content.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            return $"<!doctype html><html><head><meta charset=\"utf-8\">{styleTag}</head><body>{content}</body></html>";
        }

        if (styleTag.Length > 0 && content.Contains("</head>", StringComparison.OrdinalIgnoreCase))
        {
            content = content.Replace("</head>", $"{styleTag}</head>", StringComparison.OrdinalIgnoreCase);
        }
        else if (styleTag.Length > 0)
        {
            var htmlStart = content.IndexOf('>');
            if (htmlStart >= 0)
            {
                content = content.Insert(htmlStart + 1, $"<head>{styleTag}</head>");
            }
        }

        if (!content.Contains("<body", StringComparison.OrdinalIgnoreCase))
        {
            if (content.Contains("</head>", StringComparison.OrdinalIgnoreCase))
            {
                content = content.Replace("</head>", "</head><body>", StringComparison.OrdinalIgnoreCase);
            }
            if (content.Contains("</html>", StringComparison.OrdinalIgnoreCase))
            {
                content = content.Replace("</html>", "</body></html>", StringComparison.OrdinalIgnoreCase);
            }
        }

        return content;
    }
}
