using System.Globalization;
using System.Text;

namespace PlanSafe.App.Components.Statistics;

internal static class ChartGeometry
{
    internal static string F(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    internal static float X(float time, float maxTime) => Math.Clamp(65 + time / maxTime * 690, 65, 755);
    internal static float Y(float value, float maximum) => 302 - Math.Clamp(value, 0, maximum) / maximum * 262;
    internal static (string Band, string Mean) Paths(IEnumerable<(float Time, float Plus, float Minus, float Mean)> source, float maxTime, float maximum)
    {
        var points = source.ToArray();
        if (points.Length == 0) return ("", "");
        var band = new StringBuilder();
        var mean = new StringBuilder();
        for (int i = 0; i < points.Length; i++)
        {
            var point = points[i];
            string command = i == 0 ? "M" : " L";
            band.Append($"{command} {F(X(point.Time, maxTime))} {F(Y(point.Plus, maximum))}");
            mean.Append($"{command} {F(X(point.Time, maxTime))} {F(Y(point.Mean, maximum))}");
        }
        for (int i = points.Length - 1; i >= 0; i--)
            band.Append($" L {F(X(points[i].Time, maxTime))} {F(Y(points[i].Minus, maximum))}");
        band.Append(" Z");
        return (band.ToString(), mean.ToString());
    }
}
