using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using PlanSafe.App.Simulation;

namespace PlanSafe.Tests;

public class BottleneckValidationSimulationTests
{
    private readonly ITestOutputHelper _output;

    public BottleneckValidationSimulationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private class DensitySpeedSample
    {
        public double Density { get; set; }
        public double ActualSpeed { get; set; }
        public double TheoreticalSpeed { get; set; }
        public double PosX { get; set; }
        public double PosY { get; set; }
    }

    [Fact]
    public void Validate_1000Agents_200mWorld_SpeedVsDensity_MatchesEmpiricalGraph()
    {
        double width = 200.0;
        double height = 200.0;
        int agentCount = 1000;

        var engine = new CrowdSimulationEngine(width, height, agentCount);
        engine.InitializeAgents(seed: 12345);

        double dt = 0.05; // 50ms timestep
        int warmupSteps = 750; // 37.5 seconds of simulation for 1000 agents to reach bottleneck at X=70m
        int sampleSteps = 850; // 42.5 seconds of dense bottleneck queueing & compression (t = 37.5s to 80.0s)

        for (int step = 0; step < warmupSteps; step++)
        {
            engine.UpdatePhysics(dt);
        }

        List<DensitySpeedSample> allSamples = new();

        // Sample during the dense bottleneck queue phase
        for (int step = 0; step < sampleSteps; step++)
        {
            engine.UpdatePhysics(dt);

            // Sample every 15 steps (every 0.75s of simulated time)
            if (step % 15 == 0)
            {
                for (int i = 0; i < agentCount; i++)
                {
                    double vx = engine.AgentVelocityX[i];
                    double vy = engine.AgentVelocityY[i];
                    double speed = Math.Sqrt(vx * vx + vy * vy);
                    double density = engine.AgentLocalDensity[i];
                    double theoSpeed = PedestrianFundamentalDiagram.CalculateSpeed(density, engine.AgentMaxSpeed[i]);

                    allSamples.Add(new DensitySpeedSample
                    {
                        Density = density,
                        ActualSpeed = speed,
                        TheoreticalSpeed = theoSpeed,
                        PosX = engine.AgentPositionX[i],
                        PosY = engine.AgentPositionY[i]
                    });
                }
            }
        }

        // Define empirical density bins
        var bins = new (string Name, double MinDensity, double MaxDensity)[]
        {
            ("Regime I   (0.0 - 0.70 os/m²) - Strefa komfortu", 0.00, 0.70),
            ("Regime IIa (0.70 - 1.50 os/m²) - Lekkie spowolnienie", 0.70, 1.50),
            ("Regime IIb (1.50 - 2.20 os/m²) - Ruch ograniczony", 1.50, 2.20),
            ("Regime IIIa(2.20 - 3.50 os/m²) - Kolejka w zatorze", 2.20, 3.50),
            ("Regime IIIb(3.50 - 4.70 os/m²) - Gęsty zator", 3.50, 4.70),
            ("Regime IV  (> 4.70 os/m²)      - Ścisk / Zablokowanie", 4.70, 10.00)
        };

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("==========================================================================================================================");
        sb.AppendLine("   WALIDACJA EMPIRYCZNA: 1000 AGENTÓW, ŚWIAT 200m x 200m (DIAGRAM WEIDMANNA / SEYFRIEDA)");
        sb.AppendLine("==========================================================================================================================");
        sb.AppendLine(string.Format("{0,-48} | {1,8} | {2,12} | {3,12} | {4,10} | {5,14}",
            "Przedział Gęstości", "Próbki", "Śr. V_sym [m/s]", "Śr. V_teor [m/s]", "Różnica", "Błąd wzgl. [%]"));
        sb.AppendLine(new string('-', 122));

        List<double> avgSimSpeeds = new();
        List<double> relErrors = new();

        foreach (var bin in bins)
        {
            var binSamples = allSamples.Where(s => s.Density >= bin.MinDensity && s.Density < bin.MaxDensity).ToList();
            if (binSamples.Count > 0)
            {
                double avgSim = binSamples.Average(s => s.ActualSpeed);
                double avgTheo = binSamples.Average(s => s.TheoreticalSpeed);
                double diff = avgSim - avgTheo;
                double relErrorPercent = avgTheo > 0.0001 ? (Math.Abs(avgSim - avgTheo) / avgTheo) * 100.0 : 0.0;

                avgSimSpeeds.Add(avgSim);
                relErrors.Add(relErrorPercent);

                sb.AppendLine(string.Format("{0,-48} | {1,8} | {2,12:F3} | {3,12:F3} | {4,10:F3} | {5,13:F1}%",
                    bin.Name, binSamples.Count, avgSim, avgTheo, diff, relErrorPercent));
            }
            else
            {
                sb.AppendLine(string.Format("{0,-48} | {1,8} | {2,12} | {3,12} | {4,10} | {5,14}",
                    bin.Name, 0, "Brak", "Brak", "-", "-"));
            }
        }

        sb.AppendLine("==========================================================================================================================");
        _output.WriteLine(sb.ToString());

        // Asercje walidacyjne:
        // 1. Prędkość w strefie wolnej (Regime I) musi być wysoka (~1.2 - 1.4 m/s)
        Assert.True(avgSimSpeeds[0] > 1.05, $"Prędkość swobodna ({avgSimSpeeds[0]:F2} m/s) powinna być > 1.05 m/s");

        // 2. Monotoniczność: Prędkość w kolejnych gęstszych przedziałach musi ściśle spadać
        for (int i = 0; i < avgSimSpeeds.Count - 1; i++)
        {
            Assert.True(avgSimSpeeds[i] > avgSimSpeeds[i + 1],
                $"Prędkość w przedziale {i} ({avgSimSpeeds[i]:F3} m/s) musi być większa niż w gęstszym przedziale {i + 1} ({avgSimSpeeds[i + 1]:F3} m/s)");
        }

        // 3. Stosunek prędkości między strefą swobodną a gęstym zatorem w wąskim gardle musi być co najmniej 10x
        double speedRatio = avgSimSpeeds[0] / avgSimSpeeds[avgSimSpeeds.Count - 1];
        Assert.True(speedRatio >= 10.0,
            $"Stosunek prędkości swobodnej do zatoru ({speedRatio:F2}x) musi wynosić co najmniej 10.0x, potwierdzając silne wyhamowanie w zatorze.");

        // 4. Asercje Błędu Względnego Procentowego (Relative Percentage Error):
        // Zamiast tolerancji bezwzględnej, weryfikujemy dopasowanie procentowe:
        // Regime I: błąd względny < 10%
        Assert.True(relErrors[0] < 10.0, $"Regime I błąd względny ({relErrors[0]:F1}%) powinien być < 10.0%");

        // Regime IIa: błąd względny < 15%
        Assert.True(relErrors[1] < 15.0, $"Regime IIa błąd względny ({relErrors[1]:F1}%) powinien być < 15.0%");

        // Regime IIb: błąd względny < 20%
        Assert.True(relErrors[2] < 20.0, $"Regime IIb błąd względny ({relErrors[2]:F1}%) powinien być < 20.0%");

        // Regime IIIa: błąd względny < 20%
        Assert.True(relErrors[3] < 20.0, $"Regime IIIa błąd względny ({relErrors[3]:F1}%) powinien być < 20.0%");

        // Regime IIIb: błąd względny < 25%
        Assert.True(relErrors[4] < 25.0, $"Regime IIIb błąd względny ({relErrors[4]:F1}%) powinien być < 25.0%");

        // Regime IV: błąd względny < 35% (wyeliminowano błąd rzędu 350%)
        Assert.True(relErrors[5] < 35.0, $"Regime IV błąd względny ({relErrors[5]:F1}%) powinien być < 35.0% (wobec wcześniejszych 350%)");
    }
}
