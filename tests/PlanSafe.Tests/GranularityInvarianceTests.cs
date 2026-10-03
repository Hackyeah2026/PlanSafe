using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using PlanSafe.App.Simulation;

namespace PlanSafe.Tests;

/// <summary>
/// Testy weryfikujące niezmienniczość makroskopowego zachowania tłumu względem granulacji:
/// Granulacja (G = 1..25) powinna wpływać wyłącznie na rozdzielczość numeryczną symulacji
/// (liczbę symulowanych węzłów/kropek), a nie na zachowanie makroskopowe:
/// - Zależność prędkości od rzeczywistej gęstości tłumu (zgodność z krzywą Weidmanna)
/// - Powstawanie zatoru (kolejki / blockage) przed wąskimi gardłami
/// - Spadek prędkości w zatorze i tworzenie łuku dekompresyjnego
/// </summary>
public class GranularityInvarianceTests
{
    private readonly ITestOutputHelper _output;

    public GranularityInvarianceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(1.0, 1.0)]  // Regime IIa: rho ~ 1.0 os/m² (ruch lekko ograniczony, v ~ 1.0 m/s)
    [InlineData(3.0, 0.35)] // Regime III: rho ~ 3.0 os/m² (gęsty zator, v ~ 0.32 m/s)
    public void SpeedAgainstDensity_IsInvariantAcrossGranularities(double targetDensity, double expectedMaxSpeed)
    {
        // Testujemy stałą gęstość tłumu targetDensity w obszarze 20m x 20m (400 m²)
        double areaWidth = 20.0;
        double areaHeight = 20.0;
        int totalCrowd = (int)(targetDensity * areaWidth * areaHeight);

        int[] granularities = { 1, 4, 10 };
        Dictionary<int, double> centerDensities = new();
        Dictionary<int, double> centerSpeeds = new();

        foreach (int g in granularities)
        {
            var engine = new CrowdSimulationEngine(50.0, 50.0, totalCrowd);
            engine.SetGranulation(g);

            int dots = engine.SimulatedAgentCount;
            int gridSide = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dots)));
            double spacing = areaWidth / gridSide;

            for (int i = 0; i < dots; i++)
            {
                int r = i / gridSide;
                int c = i % gridSide;
                engine.AgentPositionX[i] = 15.0 + c * spacing;
                engine.AgentPositionY[i] = 15.0 + r * spacing;
                engine.AgentVelocityX[i] = 0.5;
                engine.AgentVelocityY[i] = 0.0;
                engine.AgentMaxSpeed[i] = 1.34;
            }

            // 1 krok symulacji fizyki do obliczenia gęstości anizotropowej i prędkości Weidmanna
            engine.UpdatePhysics(0.05);

            // Wybieramy agentów w centrum klastra (aby wykluczyć ucieczkę brzegową)
            List<double> sampledDensities = new();
            List<double> sampledSpeeds = new();

            for (int i = 0; i < dots; i++)
            {
                double x = engine.AgentPositionX[i];
                double y = engine.AgentPositionY[i];

                if (x >= 20.0 && x <= 30.0 && y >= 20.0 && y <= 30.0)
                {
                    sampledDensities.Add(engine.AgentLocalDensity[i]);
                    double vx = engine.AgentVelocityX[i];
                    double vy = engine.AgentVelocityY[i];
                    sampledSpeeds.Add(Math.Sqrt(vx * vx + vy * vy));
                }
            }

            Assert.NotEmpty(sampledDensities);
            double meanRho = sampledDensities.Average();
            double meanV = sampledSpeeds.Average();

            centerDensities[g] = meanRho;
            centerSpeeds[g] = meanV;

            _output.WriteLine($"Cel: {targetDensity:F1} os/m² | G={g,2} ({dots,3} kropek, {sampledDensities.Count,2} w centrum): Śr. Rho={meanRho:F2} os/m², Śr. V={meanV:F3} m/s");
        }

        // 1. Gęstości w centrum powinny być zbliżone do zadanej gęstości fizycznej targetDensity
        foreach (int g in granularities)
        {
            double rho = centerDensities[g];
            Assert.True(rho >= targetDensity * 0.65,
                $"Dla G={g} gęstość w centrum ({rho:F2}) jest zbyt mała względem celu ({targetDensity:F1})");
        }

        // 2. Prędkości w centrum powinny być ściśle ograniczone przez krzywą Weidmanna
        foreach (int g in granularities)
        {
            double v = centerSpeeds[g];
            Assert.True(v <= expectedMaxSpeed * 1.35,
                $"Dla G={g} prędkość ({v:F3} m/s) przekroczyła oczekiwany limit Weidmanna ({expectedMaxSpeed:F3} m/s)");
        }

        // 3. Różnica prędkości między G=1 a wyższymi granulacjami powinna być minimalna (< 30%)
        double v1 = centerSpeeds[1];
        foreach (int g in granularities.Skip(1))
        {
            double relDiff = Math.Abs(centerSpeeds[g] - v1) / v1 * 100.0;
            _output.WriteLine($"Różnica prędkości G={g} vs G=1: {relDiff:F1}%");
            Assert.True(relDiff < 30.0,
                $"Prędkość dla G={g} ({centerSpeeds[g]:F3} m/s) różni się o {relDiff:F1}% od G=1 ({v1:F3} m/s)");
        }
    }

    [Fact]
    public void BottleneckBlockage_FormsSimilarlyForGranularity1And5()
    {
        double width = 200.0;
        double height = 200.0;
        int agentCount = 1000;
        double dt = 0.05;

        // Porównujemy formowanie zatoru przed wąskim gardłem (X=70, Y=100) dla G=1 i G=5
        int[] granularities = { 1, 5 };
        Dictionary<int, double> queueSpeeds = new();
        Dictionary<int, double> queueDensities = new();
        Dictionary<int, int> queueCounts = new();

        foreach (int g in granularities)
        {
            var engine = new CrowdSimulationEngine(width, height, agentCount);
            engine.SetGranulation(g);
            engine.InitializeAgents(seed: 42);

            // 800 kroków (40 sekund symulacji) - tłum dociera do wąskiego gardła i tworzy zwarty zator
            for (int step = 0; step < 800; step++)
            {
                engine.UpdatePhysics(dt);
            }

            int dots = engine.SimulatedAgentCount;
            List<double> speedsInQueue = new();
            List<double> densitiesInQueue = new();

            // Bezpośrednia strefa zatoru przed wąskim gardłem: X in [55, 70], Y in [85, 115]
            for (int i = 0; i < dots; i++)
            {
                double x = engine.AgentPositionX[i];
                double y = engine.AgentPositionY[i];

                if (x >= 55.0 && x <= 70.0 && y >= 85.0 && y <= 115.0)
                {
                    double vx = engine.AgentVelocityX[i];
                    double vy = engine.AgentVelocityY[i];
                    speedsInQueue.Add(Math.Sqrt(vx * vx + vy * vy));
                    densitiesInQueue.Add(engine.AgentLocalDensity[i]);
                }
            }

            Assert.NotEmpty(speedsInQueue);
            double avgV = speedsInQueue.Average();
            double avgRho = densitiesInQueue.Average();

            queueSpeeds[g] = avgV;
            queueDensities[g] = avgRho;
            queueCounts[g] = speedsInQueue.Count;

            _output.WriteLine($"Zator przy G={g,2}: Kropki w zatorze={speedsInQueue.Count} (reprezentujące ~{speedsInQueue.Count * g} osób), Śr. Prędkość={avgV:F3} m/s, Śr. Gęstość={avgRho:F2} os/m²");
        }

        // 1. Liczba osób w zatorze (kropki * g) powinna być bardzo zbliżona dla obu granulacji (< 25% różnicy)
        int effectiveCrowd1 = queueCounts[1] * 1;
        int effectiveCrowd5 = queueCounts[5] * 5;
        double crowdDiff = Math.Abs(effectiveCrowd5 - effectiveCrowd1) / (double)effectiveCrowd1 * 100.0;
        _output.WriteLine($"Reprezentacja ludzi w zatorze: G=1: {effectiveCrowd1}, G=5: {effectiveCrowd5} (różnica {crowdDiff:F1}%)");
        Assert.True(crowdDiff < 25.0, $"Liczba reprezentowanych ludzi w zatorze różni się o {crowdDiff:F1}%!");

        // 2. W obu przypadkach prędkość w strefie zatoru musi silnie spaść (< 0.90 m/s wobec 1.34 m/s w ruchu swobodnym)
        Assert.True(queueSpeeds[1] < 0.90, $"G=1: prędkość w zatorze ({queueSpeeds[1]:F3} m/s) powinna być < 0.90 m/s");
        Assert.True(queueSpeeds[5] < 0.92, $"G=5: prędkość w zatorze ({queueSpeeds[5]:F3} m/s) powinna być < 0.92 m/s");

        // 3. Prędkości w zatorze między G=1 a G=5 muszą być zbliżone (różnica bezwzględna < 0.35 m/s ze względu na dyskretyzację 379 vs 75 cząstek)
        double speedDiff = Math.Abs(queueSpeeds[5] - queueSpeeds[1]);
        _output.WriteLine($"Różnica prędkości w zatorze między G=1 a G=5: {speedDiff:F3} m/s");
        Assert.True(speedDiff < 0.35, $"Różnica prędkości w zatorze ({speedDiff:F3} m/s) jest zbyt duża. Zator powinien zachowywać się analogicznie.");
    }

    [Fact]
    public void ExtremeGranulation_2000Agents_SteadilyProgressesWithoutWigglingInPlace()
    {
        // Sprawdzamy ekstremalną granulację G = 25 dla 2000 agentów (80 makro-kropek)
        // Agenci muszą stabilnie przesuwać się w kierunku ewakuacji (+X), a nie drgać w miejscu.
        double width = 200.0;
        double height = 200.0;
        int agentCount = 2000;
        int granulation = 25;

        var engine = new CrowdSimulationEngine(width, height, agentCount);
        engine.SetGranulation(granulation);
        engine.InitializeAgents(seed: 12345);

        int dots = engine.SimulatedAgentCount;
        Assert.Equal(80, dots);

        double initialMeanX = Enumerable.Range(0, dots).Average(i => engine.AgentPositionX[i]);

        double dt = 0.05;
        // Symulacja przez 400 kroków (20 sekund)
        for (int step = 0; step < 400; step++)
        {
            engine.UpdatePhysics(dt);
            if (step == 0 || step == 1 || step == 4 || step == 5 || step == 10 || step == 50)
            {
                var flow = engine.PotentialFieldMap.GetFlowDirection(engine.AgentPositionX[0], engine.AgentPositionY[0]);
                double meanVxStep = Enumerable.Range(0, dots).Average(i => engine.AgentVelocityX[i]);
                double meanRhoStep = Enumerable.Range(0, dots).Average(i => engine.AgentLocalDensity[i]);
                _output.WriteLine($"Krok {step,3}: Śr. Vx={meanVxStep:F3}, Flow[0]=({flow.dx:F3}, {flow.dy:F3}), Pos[0]=({engine.AgentPositionX[0]:F1}, {engine.AgentPositionY[0]:F1}), Vel[0]=({engine.AgentVelocityX[0]:F3}, {engine.AgentVelocityY[0]:F3})");
            }
        }

        double finalMeanX = Enumerable.Range(0, dots).Average(i => engine.AgentPositionX[i]);
        double distanceAdvanced = finalMeanX - initialMeanX;

        _output.WriteLine($"G=25 (2000 agentów, 80 kropek): Pozycja startowa X={initialMeanX:F1}m, Pozycja po 20s X={finalMeanX:F1}m, Postęp={distanceAdvanced:F1}m");

        // 1. Postęp w osi X musi być wyraźny (co najmniej 15 metrów w przód)
        Assert.True(distanceAdvanced >= 15.0,
            $"Agenci nie zrobili wystarczającego postępu w osi X ({distanceAdvanced:F1}m < 15m) — prawdopodobnie utknęli lub drgają w miejscu!");

        // 2. Składowa prędkości wzdłużnej Vx musi dominować nad składową poprzeczną Vy (brak dominacji wiggle)
        List<double> vxList = new();
        List<double> vyAbsList = new();
        for (int i = 0; i < dots; i++)
        {
            vxList.Add(engine.AgentVelocityX[i]);
            vyAbsList.Add(Math.Abs(engine.AgentVelocityY[i]));
        }

        double meanVx = vxList.Average();
        double meanVyAbs = vyAbsList.Average();
        _output.WriteLine($"Śr. Vx={meanVx:F3} m/s, Śr. |Vy|={meanVyAbs:F3} m/s");

        Assert.True(meanVx > 0.40, $"Średnia prędkość postępowa Vx ({meanVx:F3} m/s) powinna być > 0.40 m/s");
        Assert.True(meanVx > meanVyAbs, $"Składowa poprzeczna |Vy| ({meanVyAbs:F3} m/s) przewyższa składową postępową Vx ({meanVx:F3} m/s), co oznacza zjawisko 'wiggle'!");
    }

    [Fact]
    public void MaxAllowedAgents_Supports100000Agents()
    {
        var engine = new CrowdSimulationEngine(1000.0, 1000.0, 100000);
        Assert.Equal(100000, engine.AgentCount);
        Assert.Equal(100000, engine.SimulatedAgentCount);
        Assert.Equal(100000, CrowdSimulationEngine.MaxAllowedAgents);

        // Step simulation to verify all 100000 slots update without index errors
        engine.Step(0.016, 1.0);
        Assert.True(engine.AgentPositionX[99999] > 0);
    }
}
