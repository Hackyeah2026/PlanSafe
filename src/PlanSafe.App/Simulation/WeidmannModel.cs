namespace PlanSafe.App.Simulation;

/// <summary>
/// Implements the empirical Weidmann (1993) pedestrian speed-density relation:
/// v(ρ) = v0 * [1 - exp(-γ * (1/ρ - 1/ρ_max))]
/// </summary>
public static class WeidmannModel
{
    public const float DefaultFreeSpeed = 1.34f;     // v0: Free-flow walking speed in m/s
    public const float DefaultJamDensity = 5.4f;      // ρ_max: Jam density in ped/m²
    public const float DefaultGamma = 1.913f;         // γ: Empirical decay coefficient in ped/m²

    /// <summary>
    /// Calculates desired walking speed given local crowd density.
    /// </summary>
    /// <param name="density">Local pedestrian density in ped/m².</param>
    /// <param name="v0">Unimpeded free-flow speed (m/s).</param>
    /// <param name="rhoMax">Maximum jam density (ped/m²).</param>
    /// <param name="gamma">Empirical shape coefficient (ped/m²).</param>
    /// <returns>Speed in m/s, strictly in [0, v0].</returns>
    public static float CalculateSpeed(
        float density,
        float v0 = DefaultFreeSpeed,
        float rhoMax = DefaultJamDensity,
        float gamma = DefaultGamma)
    {
        if (density <= 0f)
        {
            return v0;
        }

        if (density >= rhoMax)
        {
            return 0f;
        }

        double exponent = -gamma * ((1.0 / density) - (1.0 / rhoMax));
        double factor = 1.0 - Math.Exp(exponent);

        if (factor <= 0.0)
        {
            return 0f;
        }

        float speed = (float)(v0 * factor);
        return Math.Clamp(speed, 0f, v0);
    }
}
