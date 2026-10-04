using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PlanSafe.Api.Services;
using PlanSafe.Contracts;
using Xunit;
using ZXing;
using ZXing.Common;

namespace PlanSafe.Api.Tests;

public class QrCodeSvgTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(140)]
    [InlineData(200)]
    [InlineData(400)]
    public void GeneratedSvg_DecodesToCompleteUrl(int pathLength)
    {
        string url = "https://plansafe.example/" + new string('a', pathLength) + "/evacuate?session=abcdef12";
        string svg = QrCodeSvgGenerator.GenerateSvg(url);
        Assert.Equal(url, DecodeSvg(svg));
        Assert.Equal(svg, QrCodeSvg.Generate(url));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/evacuate?session=abcdef12")]
    [InlineData("https://plansafe.example/evacuate")]
    [InlineData("https://plansafe.example/ewakuacja/Łódź?session=żółć&name=安全")]
    public void GeneratedSvg_DecodesAtShareCardSize(string url)
    {
        Assert.Equal(url, DecodeSvg(QrCodeSvgGenerator.GenerateSvg(url), 182));
    }

    [Fact]
    public void GeneratedSvg_PreservesCustomQuietZone()
    {
        const string url = "https://plansafe.example/evacuate";
        Assert.Equal(url, DecodeSvg(QrCodeSvgGenerator.GenerateSvg(url, 8), margin: 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => QrCodeSvgGenerator.GenerateSvg(url, 3));
    }

    [Fact]
    public void GeneratedSvg_RejectsOversizedPayloadInsteadOfTruncatingIt()
    {
        Assert.Throws<QRCoder.Exceptions.DataTooLongException>(() =>
            QrCodeSvgGenerator.GenerateSvg(new string('a', 4000)));
    }

    internal static string DecodeSvg(string svg, int? pixelSize = null, int margin = 4)
    {
        // Rasterize the emitted SVG modules, then scan with an independent barcode reader.
        var root = XElement.Parse(svg);
        int size = int.Parse(root.Attribute("viewBox")!.Value.Split(' ')[2], CultureInfo.InvariantCulture);
        var background = root.Element(root.Name.Namespace + "rect")!;
        Assert.Equal("#ffffff", background.Attribute("fill")!.Value);
        var path = root.Element(root.Name.Namespace + "path")!;
        Assert.Equal("#000000", path.Attribute("fill")!.Value);
        var modules = new bool[size, size];
        foreach (Match match in Regex.Matches(path.Attribute("d")!.Value, @"M(\d+),(\d+)h1v1h-1z"))
        {
            int x = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int y = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            Assert.InRange(x, margin, size - margin - 1);
            Assert.InRange(y, margin, size - margin - 1);
            modules[y, x] = true;
        }
        int pixels = pixelSize ?? size * 4;
        var rgb = new byte[pixels * pixels * 3];
        for (int y = 0; y < pixels; y++)
        {
            for (int x = 0; x < pixels; x++)
            {
                byte value = modules[y * size / pixels, x * size / pixels] ? (byte)0 : (byte)255;
                int index = (y * pixels + x) * 3;
                rgb[index] = rgb[index + 1] = rgb[index + 2] = value;
            }
        }
        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE] }
        };
        var result = reader.Decode(new RGBLuminanceSource(rgb, pixels, pixels, RGBLuminanceSource.BitmapFormat.RGB24));
        Assert.NotNull(result);
        return result.Text;
    }
}
