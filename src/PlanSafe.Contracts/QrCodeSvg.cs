using System.Text;
using QRCoder;

namespace PlanSafe.Contracts;

/// <summary>Shared QR encoding for published plans and the browser demo.</summary>
public static class QrCodeSvg
{
    public static string Generate(string content, int margin = 4)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(margin, 4);

        using var data = QRCodeGenerator.GenerateQrCode(content, QRCodeGenerator.ECCLevel.Q,
            forceUtf8: true, eciMode: QRCodeGenerator.EciMode.Utf8);

        // QRCoder includes a four-module quiet zone. Preserve it and add any extra margin.
        int offset = margin - 4;
        int size = checked(data.ModuleMatrix.Count + offset * 2);
        var svg = new StringBuilder();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" shape-rendering=\"crispEdges\">");
        svg.Append($"<rect width=\"{size}\" height=\"{size}\" fill=\"#ffffff\"/>");
        svg.Append("<path fill=\"#000000\" d=\"");
        for (int row = 0; row < data.ModuleMatrix.Count; row++)
        {
            for (int col = 0; col < data.ModuleMatrix.Count; col++)
            {
                if (data.ModuleMatrix[row][col])
                    svg.Append($"M{col + offset},{row + offset}h1v1h-1z");
            }
        }
        svg.Append("\"/></svg>");
        return svg.ToString();
    }
}
