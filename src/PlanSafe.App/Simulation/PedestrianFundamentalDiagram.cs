namespace PlanSafe.App.Simulation;

/// <summary>
/// Implementacja empirycznego diagramu fundamentalnego prędkość-gęstość ruchu pieszych (Weidmann / Seyfried).
/// Definiuje 4 reżimy przepływu tłumu:
/// Regime I:   rho in [0.0, 0.70]  - Strefa komfortu / Swobodny marsz (v: 1.34 -> 1.14 m/s)
/// Regime II:  rho in (0.70, 2.20] - Ruch ograniczony / Strome wyhamowanie (v: 1.14 -> 0.47 m/s)
/// Regime III: rho in (2.20, 4.70] - Gęsty tłum / Szuranie w zatorze (v: 0.47 -> 0.15 m/s)
/// Regime IV:  rho in (4.70, 5.40] - Zablokowanie / Spadek do zera (v -> 0.0 m/s przy rho >= 5.40)
/// </summary>
public static class PedestrianFundamentalDiagram
{
    public const double ComfortDensityThreshold = 0.70;       // [os/m²] Granica strefy komfortu (Regime I/II)
    public const double ConstrainedDensityThreshold = 2.20;   // [os/m²] Granica przepływu ograniczonego (Regime II/III)
    public const double DenseCrowdThreshold = 4.70;           // [os/m²] Granica gęstego tłumu (Regime III/IV)
    public const double JamDensity = 5.40;                   // [os/m²] Gęstość zakleszczenia (całkowite zatrzymanie)

    public const double DefaultFreeSpeed = 1.34;             // [m/s] Bazowa prędkość swobodnego marszu przy rho = 0

    /// <summary>
    /// Oblicza znormalizowany współczynnik prędkości v / v0 w zakresie [0.0, 1.0] na podstawie gęstości rho [os/m²].
    /// Krzywa ściśle odzwierciedla dane empiryczne z 4 reżimami ruchu.
    /// </summary>
    public static double CalculateSpeedFactor(double density)
    {
        if (density <= 0.0) return 1.0;
        if (density >= JamDensity) return 0.0;

        // REGIME I: Strefa komfortu (rho in [0.0, 0.70])
        // Prędkość spada łagodnie z 1.0 (1.34 m/s) do 0.851 (1.14 m/s)
        if (density <= ComfortDensityThreshold)
        {
            double t = density / ComfortDensityThreshold;
            return 1.0 - 0.149 * Math.Pow(t, 1.6);
        }

        // REGIME II: Ruch ograniczony (rho in (0.70, 2.20])
        // Prędkość spada stromo z 0.851 (1.14 m/s) do 0.351 (0.47 m/s)
        if (density <= ConstrainedDensityThreshold)
        {
            double t = (density - ComfortDensityThreshold) / (ConstrainedDensityThreshold - ComfortDensityThreshold);
            return 0.851 - 0.500 * (t * (1.0 + 0.15 * (1.0 - t)));
        }

        // REGIME III: Gęsty tłum / Kolejka (rho in (2.20, 4.70])
        // Prędkość spada wolniej z 0.351 (0.47 m/s) do 0.112 (0.15 m/s)
        if (density <= DenseCrowdThreshold)
        {
            double t = (density - ConstrainedDensityThreshold) / (DenseCrowdThreshold - ConstrainedDensityThreshold);
            double curve = Math.Pow(1.0 - t, 1.4);
            return 0.112 + (0.351 - 0.112) * curve;
        }

        // REGIME IV: Zakleszczenie / Zatrzymanie (rho in (4.70, 5.40])
        // Prędkość spada ostro z 0.112 (0.15 m/s) do 0.00 m/s przy rho = 5.40
        {
            double t = (density - DenseCrowdThreshold) / (JamDensity - DenseCrowdThreshold);
            return Math.Max(0.0, 0.112 * (1.0 - Math.Pow(t, 1.2)));
        }
    }

    /// <summary>
    /// Oblicza docelową prędkość marszu [m/s] dla danej gęstości tłumu.
    /// </summary>
    public static double CalculateSpeed(double density, double freeSpeed = DefaultFreeSpeed)
    {
        return freeSpeed * CalculateSpeedFactor(density);
    }

    /// <summary>
    /// Zwraca numer reżimu ruchu (1, 2, 3 lub 4) dla zadanej gęstości.
    /// </summary>
    public static int GetRegime(double density)
    {
        if (density <= ComfortDensityThreshold) return 1;
        if (density <= ConstrainedDensityThreshold) return 2;
        if (density <= DenseCrowdThreshold) return 3;
        return 4;
    }

    /// <summary>
    /// Oblicza efektywną gęstość lokalną [os/m²] na podstawie gęstości w stożku przednim i odległości do lidera z przodu.
    /// Zapewnia, że agenci na czele grupy (z pustą przestrzenią przed sobą) nie są spowalniani przez tłum idący za nimi.
    /// </summary>
    /// <param name="forwardKernelSum">Gęstość jądrowa w stożku przednim (kierunek ruchu)</param>
    /// <param name="closestForwardDistance">Odległość do najbliższego agenta bezpośrednio z przodu w stożku ruchu (jeśli brak, duża wartość np. 10.0)</param>
    public static double CalculateEffectiveDensity(double forwardKernelSum, double closestForwardDistance)
    {
        double forwardDensity = 0.0;
        if (closestForwardDistance > 0.05 && closestForwardDistance < 1.8)
        {
            forwardDensity = 1.20 / (closestForwardDistance * closestForwardDistance);
        }

        // W swobodnym marszu lub pojedynczej kolumnie (forwardKernelSum <= ConstrainedDensityThreshold),
        // pojedynczy poprzednik nie wprowadza agenta w zator Regime IV.
        // Dopiero w rzeczywistym, wielowarstwowym zatorze (forwardKernelSum > 2.20) bliski poprzednik powoduje zablokowanie.
        if (forwardKernelSum <= ConstrainedDensityThreshold)
        {
            forwardDensity = Math.Min(forwardDensity, ConstrainedDensityThreshold);
        }
        else
        {
            forwardDensity = Math.Min(forwardDensity, JamDensity - 0.05);
        }

        return Math.Max(forwardKernelSum, forwardDensity);
    }

    /// <summary>
    /// Stany behawioralne dynamiki tłumu wg Üsten, Lügering, Sieben (2022).
    /// </summary>
    public enum PushingBehaviorState
    {
        FallingBehind = 1, // Pozostawanie w tyle (zwalnianie / utrata tempa)
        JustWalking = 2,   // Swobodny marsz bez kontaktu i bez popychania (rho <= 0.70)
        MildPushing = 3,   // Umiarkowane popychanie kontaktowe (0.70 < rho <= 2.50)
        StrongPushing = 4  // Silne popychanie i propagacja fal nacisku (rho > 2.50)
    }

    /// <summary>
    /// Określa stan behawioralny agenta wg klasyfikacji Üsten et al. (2022).
    /// </summary>
    public static PushingBehaviorState DeterminePushingState(double density, double headwayDistance, double contactThreshold = 0.80)
    {
        if (density <= ComfortDensityThreshold || headwayDistance > contactThreshold)
        {
            return PushingBehaviorState.JustWalking;
        }
        if (density <= 2.50)
        {
            return PushingBehaviorState.MildPushing;
        }
        return PushingBehaviorState.StrongPushing;
    }

    /// <summary>
    /// Oblicza współczynnik intensywności popychania Phi(rho) na podstawie empirycznych badań
    /// Üsten, Lügering, Sieben (2022) oraz Sieben & Seyfried (Safety Science 2023).
    /// - rho <= 0.70: Phi = 0.0 (Just Walking, brak popychania)
    /// - 0.70 < rho <= 2.50: Phi rośnie liniowo od 0.0 do 1.0 (Mild Pushing)
    /// - rho > 2.50: Phi rośnie nieliniowo powyżej 1.0 (Strong Pushing & propagacja fal nacisku)
    /// </summary>
    public static double CalculatePushingFactor(double density)
    {
        if (density <= ComfortDensityThreshold) return 0.0;
        if (density <= 2.50)
        {
            return (density - ComfortDensityThreshold) / (2.50 - ComfortDensityThreshold);
        }
        return 1.0 + 0.85 * (density - 2.50);
    }

    /// <summary>
    /// Oblicza wielkość siły popychania wywieranej przez agenta z tyłu na agenta z przodu.
    /// Zgodnie z badaniami Üsten et al. (2022) i Sieben & Seyfried (2023), w gęstym tłumie
    /// pęd i motywacja jednostki z tyłu przekazywane są fizycznie w przód.
    /// </summary>
    /// <param name="density">Lokalna gęstość tłumu</param>
    /// <param name="distance">Odległość między środkami agentów</param>
    /// <param name="radiusSum">Suma promieni obu agentów</param>
    /// <param name="contactBuffer">Zasięg kontaktu ciał/ramion (domyślnie 0.25m)</param>
    /// <param name="rearForwardDrive">Składowa motywacji/prędkości agenta z tyłu w kierunku agenta z przodu</param>
    public static double CalculateRearPushingForce(double density, double distance, double radiusSum, double contactBuffer = 0.25, double rearForwardDrive = 1.0)
    {
        double pushingFactor = CalculatePushingFactor(density);
        if (pushingFactor <= 0.0) return 0.0;

        double contactThreshold = radiusSum + contactBuffer;
        if (distance >= contactThreshold || distance <= 0.0001) return 0.0;

        // Siła sprężystości kontaktowej ciał
        double penetration = Math.Max(0.0, radiusSum - distance);
        double contactPush = penetration * 4.0;

        // Siła wynikająca z naporu pędu / motywacji agenta z tyłu
        double proximityFactor = (contactThreshold - distance) / contactBuffer;
        double drivePush = Math.Max(0.0, rearForwardDrive) * 1.5 * proximityFactor;

        return pushingFactor * (contactPush + drivePush);
    }
}
