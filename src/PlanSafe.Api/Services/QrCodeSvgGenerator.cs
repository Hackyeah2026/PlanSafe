namespace PlanSafe.Api.Services;

/// <summary>Produces a scannable SVG containing the complete evacuation URL.</summary>
public static class QrCodeSvgGenerator
{
    public static string GenerateSvg(string content, int margin = 4)
        => PlanSafe.Contracts.QrCodeSvg.Generate(content, margin);
}
