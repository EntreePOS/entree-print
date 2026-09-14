using System.Net;
using System.Text;

namespace EntreePrintPlugin.Services;

public static class Code128Barcode
{
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112"
    ];

    public static string ToSvg(string value, int height, int moduleWidth)
    {
        if (value.Any(character => character < 32 || character > 126))
        {
            throw new CommandException("CONTENT_INVALID", "Code128 barcode only supports printable ASCII in v1.");
        }

        height = Math.Clamp(height, 24, 240);
        moduleWidth = Math.Clamp(moduleWidth, 1, 8);

        var codes = new List<int> { 104 };
        codes.AddRange(value.Select(character => character - 32));

        var checksum = codes[0];
        for (var index = 1; index < codes.Count; index++)
        {
            checksum += codes[index] * index;
        }
        codes.Add(checksum % 103);
        codes.Add(106);

        var totalModules = codes.Sum(code => Patterns[code].Sum(character => character - '0'));
        var width = totalModules * moduleWidth;
        var x = 0;
        var bars = new StringBuilder();

        foreach (var code in codes)
        {
            var pattern = Patterns[code];
            var drawBar = true;
            foreach (var character in pattern)
            {
                var modules = character - '0';
                var segmentWidth = modules * moduleWidth;
                if (drawBar)
                {
                    bars.Append($"<rect x=\"{x}\" y=\"0\" width=\"{segmentWidth}\" height=\"{height}\"/>");
                }
                x += segmentWidth;
                drawBar = !drawBar;
            }
        }

        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"barcode {WebUtility.HtmlEncode(value)}\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" style=\"max-width:100%;height:auto;display:inline-block;\" shape-rendering=\"crispEdges\"><rect width=\"100%\" height=\"100%\" fill=\"#fff\"/><g fill=\"#000\">{bars}</g></svg>";
    }
}
