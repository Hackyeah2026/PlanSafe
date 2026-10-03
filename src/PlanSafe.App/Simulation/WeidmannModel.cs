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

    public const float ComfortDensityThreshold = 0.70f;
    public const float ConstrainedDensityThreshold = 2.20f;
    public const float DenseCrowdThreshold = 4.70f;

    /// <summary>
    /// Calculates effective local density based on forward cone kernel and distance to the leader ahead.
    /// In single-file or constrained queues (forwardKernelSum <= 2.20), single leaders do not trigger Jam Regime IV.
    /// </summary>
    public static float CalculateEffectiveDensity(float forwardKernelSum, float closestForwardDistance)
    {
        float forwardDensity = 0.0f;
        if (closestForwardDistance > 0.05f && closestForwardDistance < 1.8f)
        {
            forwardDensity = 1.20f / (closestForwardDistance * closestForwardDistance);
        }

        if (forwardKernelSum <= 3.50f)
        {
            forwardDensity = Math.Min(forwardDensity, ConstrainedDensityThreshold);
        }
        else
        {
            forwardDensity = Math.Min(forwardDensity, DefaultJamDensity - 0.05f);
        }

        return Math.Max(forwardKernelSum, forwardDensity);
    }

    /// <summary>
    /// Computes pushing intensity factor Phi(rho) based on empirical crowd research.
    /// </summary>
    public static float CalculatePushingFactor(float density)
    {
        if (density <= ComfortDensityThreshold) return 0.0f;
        if (density <= 2.50f)
        {
            return (density - ComfortDensityThreshold) / (2.50f - ComfortDensityThreshold);
        }
        return 1.0f + 0.85f * (density - 2.50f);
    }

    /// <summary>
    /// Computes rear pushing force transmitted by trailing agents in dense queues.
    /// </summary>
    public static float CalculateRearPushingForce(
        float density,
        float distance,
        float radiusSum,
        float contactBuffer = 0.25f,
        float rearForwardDrive = 1.0f)
    {
        float pushingFactor = CalculatePushingFactor(density);
        if (pushingFactor <= 0.0f) return 0.0f;

        float contactThreshold = radiusSum + contactBuffer;
        if (distance >= contactThreshold || distance <= 0.0001f) return 0.0f;

        float penetration = Math.Max(0.0f, radiusSum - distance);
        float contactPush = penetration * 4.0f;

        float proximityFactor = (contactThreshold - distance) / contactBuffer;
        float drivePush = Math.Max(0.0f, rearForwardDrive) * 1.5f * proximityFactor;

        return pushingFactor * (contactPush + drivePush);
    }
}
