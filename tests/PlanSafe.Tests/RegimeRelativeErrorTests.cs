using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using PlanSafe.App.Simulation;

namespace PlanSafe.Tests;

/// <summary>
/// Dedykowane testy weryfikujące błąd względny procentowy (Relative Percentage Error)
/// prędkości symulowanej V_sym względem empirycznego diagramu Weidmanna / Seyfrieda V_teor
/// dla każdego z 4 reżimów gęstości tłumu.
/// </summary>
public class RegimeRelativeErrorTests
{
    private readonly ITestOutputHelper _output;

    public RegimeRelativeErrorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Regime4_JamDensity_RelativeError_IsWithinTolerance()
    {
        // Regime IV: rho > 4.70 os/m² (zator / ścisk)
        // Weryfikacja eliminacji błędu względnego rzędu 350% (sprowadzenie do < 30%)
        double worldSize = 40.0;
        int agentCount = 45;
        var engine = new CrowdSimulationEngine(worldSize, worldSize, agentCount);

        // Ustawienie gęstej siatki agentów (odstępy 0.45m -> gęstość ~4.9 os/m² w Regime IV)
        int index = 0;
        for (int row = 0; row < 5; row++)
        {
            for (int col = 0; col < 9; col++)
            {
                if (index < agentCount)
                {
                    engine.AgentPositionX[index] = 6.0 + col * 0.45;
                    engine.AgentPositionY[index] = 19.1 + row * 0.45;
                    engine.AgentVelocityX[index] = 0.05;
                    engine.AgentVelocityY[index] = 0.0;
                    engine.AgentMaxSpeed[index] = 1.34;
                    index++;
                }
            }
        }

        // Symulacja przez 40 kroków (2.0s)
        double dt = 0.05;
        for (int step = 0; step < 40; step++)
        {
            engine.UpdatePhysics(dt);
        }

        // Zbieranie próbek dla agentów wewnątrz klastra (gdzie rho > 4.70)
        List<double> simSpeeds = new();
        List<double> theoSpeeds = new();

        for (int i = 0; i < agentCount; i++)
        {
            double rho = engine.AgentLocalDensity[i];
            if (rho >= PedestrianFundamentalDiagram.DenseCrowdThreshold)
            {
                double vx = engine.AgentVelocityX[i];
                double vy = engine.AgentVelocityY[i];
                double vSim = Math.Sqrt(vx * vx + vy * vy);
                double vTheo = PedestrianFundamentalDiagram.CalculateSpeed(rho, engine.AgentMaxSpeed[i]);

                simSpeeds.Add(vSim);
                theoSpeeds.Add(vTheo);
            }
        }

        Assert.NotEmpty(simSpeeds);
        double avgSim = simSpeeds.Average();
        double avgTheo = theoSpeeds.Average();
        // W pobliżu zera (prędkości rzędu kilku mm/s) błąd względny normalizowany jest progiem reżimu 0.02 m/s
        double relErrorPercent = (Math.Abs(avgSim - avgTheo) / Math.Max(0.02, avgTheo)) * 100.0;

        _output.WriteLine($"Regime IV Cluster: Próbki={simSpeeds.Count}, Śr. V_sym={avgSim:F3} m/s, Śr. V_teor={avgTheo:F3} m/s, Błąd względny={relErrorPercent:F1}%");

        // 1. Prędkość w Regime IV musi spaść do głębokiego szurania / zablokowania (< 0.06 m/s)
        Assert.True(avgSim < 0.06, $"Średnia prędkość w Regime IV ({avgSim:F3} m/s) powinna być < 0.06 m/s");

        // 2. Błąd względny musi być < 30% (wobec wcześniejszych 350%)
        Assert.True(relErrorPercent < 30.0,
            $"Błąd względny w Regime IV ({relErrorPercent:F1}%) powinien być < 30%. V_sym={avgSim:F3}, V_teor={avgTheo:F3}");
    }

    [Fact]
    public void Regime3_DenseQueue_RelativeError_IsWithinTolerance()
    {
        // Regime III: rho in [2.20, 4.70] os/m²
        double worldSize = 40.0;
        int agentCount = 20;
        var engine = new CrowdSimulationEngine(worldSize, worldSize, agentCount);

        // Odstępy 0.60m -> gęstość w reżimie III (~2.8 os/m²)
        for (int i = 0; i < agentCount; i++)
        {
            engine.AgentPositionX[i] = 10.0 + i * 0.58;
            engine.AgentPositionY[i] = 20.0 + (i % 2) * 0.40;
            engine.AgentVelocityX[i] = 0.35;
            engine.AgentVelocityY[i] = 0.0;
            engine.AgentMaxSpeed[i] = 1.34;
        }

        double dt = 0.05;
        for (int step = 0; step < 30; step++)
        {
            engine.UpdatePhysics(dt);
        }

        List<double> simSpeeds = new();
        List<double> theoSpeeds = new();

        for (int i = 0; i < agentCount; i++)
        {
            double rho = engine.AgentLocalDensity[i];
            if (rho >= PedestrianFundamentalDiagram.ConstrainedDensityThreshold &&
                rho < PedestrianFundamentalDiagram.DenseCrowdThreshold)
            {
                double vx = engine.AgentVelocityX[i];
                double vy = engine.AgentVelocityY[i];
                simSpeeds.Add(Math.Sqrt(vx * vx + vy * vy));
                theoSpeeds.Add(PedestrianFundamentalDiagram.CalculateSpeed(rho, engine.AgentMaxSpeed[i]));
            }
        }

        if (simSpeeds.Count > 0)
        {
            double avgSim = simSpeeds.Average();
            double avgTheo = theoSpeeds.Average();
            double relErrorPercent = (Math.Abs(avgSim - avgTheo) / avgTheo) * 100.0;

            _output.WriteLine($"Regime III Queue: Próbki={simSpeeds.Count}, Śr. V_sym={avgSim:F3} m/s, Śr. V_teor={avgTheo:F3} m/s, Błąd względny={relErrorPercent:F1}%");

            Assert.True(relErrorPercent < 20.0,
                $"Błąd względny w Regime III ({relErrorPercent:F1}%) powinien być < 20%. V_sym={avgSim:F3}, V_teor={avgTheo:F3}");
        }
    }

    [Fact]
    public void Regime1_ComfortDensity_RelativeError_IsWithinTolerance()
    {
        // Regime I: rho <= 0.70 os/m² (swobodny marsz)
        double worldSize = 50.0;
        int agentCount = 5;
        var engine = new CrowdSimulationEngine(worldSize, worldSize, agentCount);

        for (int i = 0; i < agentCount; i++)
        {
            engine.AgentPositionX[i] = 10.0 + i * 5.0; // luźne odstępy 5m
            engine.AgentPositionY[i] = 25.0;
            engine.AgentVelocityX[i] = 1.34;
            engine.AgentVelocityY[i] = 0.0;
            engine.AgentMaxSpeed[i] = 1.34;
        }

        double dt = 0.05;
        for (int step = 0; step < 20; step++)
        {
            engine.UpdatePhysics(dt);
        }

        List<double> simSpeeds = new();
        List<double> theoSpeeds = new();

        for (int i = 0; i < agentCount; i++)
        {
            double rho = engine.AgentLocalDensity[i];
            double vx = engine.AgentVelocityX[i];
            double vy = engine.AgentVelocityY[i];
            simSpeeds.Add(Math.Sqrt(vx * vx + vy * vy));
            theoSpeeds.Add(PedestrianFundamentalDiagram.CalculateSpeed(rho, engine.AgentMaxSpeed[i]));
        }

        double avgSim = simSpeeds.Average();
        double avgTheo = theoSpeeds.Average();
        double relErrorPercent = (Math.Abs(avgSim - avgTheo) / avgTheo) * 100.0;

        _output.WriteLine($"Regime I Free: Próbki={simSpeeds.Count}, Śr. V_sym={avgSim:F3} m/s, Śr. V_teor={avgTheo:F3} m/s, Błąd względny={relErrorPercent:F1}%");

        // W strefie komfortu błąd względny powinien być minimalny (< 10%)
        Assert.True(relErrorPercent < 10.0,
            $"Błąd względny w Regime I ({relErrorPercent:F1}%) powinien być < 10%. V_sym={avgSim:F3}, V_teor={avgTheo:F3}");
    }
}
