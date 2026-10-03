using System;
using Xunit;
using PlanSafe.App.Simulation;

namespace PlanSafe.Tests;

public class PushingDynamicsTests
{
    [Fact]
    public void Regime1_JustWalking_ZeroRearPushing()
    {
        // Regime I: rho <= 0.70 os/m² (Comfort density / Just Walking)
        // In this regime, individuals respect personal space; pushing factor must be 0.
        double factorZero = PedestrianFundamentalDiagram.CalculatePushingFactor(0.0);
        double factorQuarter = PedestrianFundamentalDiagram.CalculatePushingFactor(0.25);
        double factorHalf = PedestrianFundamentalDiagram.CalculatePushingFactor(0.50);
        double factorComfortLimit = PedestrianFundamentalDiagram.CalculatePushingFactor(0.70);

        Assert.Equal(0.0, factorZero);
        Assert.Equal(0.0, factorQuarter);
        Assert.Equal(0.0, factorHalf);
        Assert.Equal(0.0, factorComfortLimit);

        // Even with physical body proximity (e.g. d = 0.75m), in comfort zone rear push is 0
        double force = PedestrianFundamentalDiagram.CalculateRearPushingForce(
            density: 0.60, distance: 0.75, radiusSum: 0.70, contactBuffer: 0.30, rearForwardDrive: 1.4);
        Assert.Equal(0.0, force);

        var state = PedestrianFundamentalDiagram.DeterminePushingState(0.60, headwayDistance: 0.50);
        Assert.Equal(PedestrianFundamentalDiagram.PushingBehaviorState.JustWalking, state);
    }

    [Fact]
    public void Regime2_MildPushing_ActivatesWhenContactBufferBreached()
    {
        // Regime II: rho in (0.70, 2.50] os/m² (Mild Pushing)
        // As density rises, personal space collapses and mild pushing emerges.
        double factor1_0 = PedestrianFundamentalDiagram.CalculatePushingFactor(1.0);
        double factor1_5 = PedestrianFundamentalDiagram.CalculatePushingFactor(1.5);
        double factor2_0 = PedestrianFundamentalDiagram.CalculatePushingFactor(2.0);
        double factor2_5 = PedestrianFundamentalDiagram.CalculatePushingFactor(2.5);

        Assert.InRange(factor1_0, 0.10, 0.25);
        Assert.InRange(factor1_5, 0.40, 0.50);
        Assert.InRange(factor2_0, 0.65, 0.80);
        Assert.Equal(1.0, factor2_5, precision: 2);

        // When rear agent is within contact buffer (d = 0.80m, radiusSum = 0.70m, buffer = 0.30m)
        double forceInsideBuffer = PedestrianFundamentalDiagram.CalculateRearPushingForce(
            density: 1.50, distance: 0.80, radiusSum: 0.70, contactBuffer: 0.30, rearForwardDrive: 1.2);
        Assert.True(forceInsideBuffer > 0.0, "Rear pushing force must be positive when contact buffer is breached");

        // When rear agent is outside contact buffer (d = 1.10m > 1.00m)
        double forceOutsideBuffer = PedestrianFundamentalDiagram.CalculateRearPushingForce(
            density: 1.50, distance: 1.10, radiusSum: 0.70, contactBuffer: 0.30, rearForwardDrive: 1.2);
        Assert.Equal(0.0, forceOutsideBuffer);

        var state = PedestrianFundamentalDiagram.DeterminePushingState(1.50, headwayDistance: 0.60);
        Assert.Equal(PedestrianFundamentalDiagram.PushingBehaviorState.MildPushing, state);
    }

    [Fact]
    public void Regime3_StrongPushing_CompressiveForceScalesWithDensity()
    {
        // Regime III & IV: rho > 2.50 os/m² (Strong Pushing & Compressive Force Waves)
        // High density creates intense pushing forces that scale non-linearly with crowd pressure.
        double factor3_0 = PedestrianFundamentalDiagram.CalculatePushingFactor(3.0);
        double factor4_0 = PedestrianFundamentalDiagram.CalculatePushingFactor(4.0);
        double factor5_0 = PedestrianFundamentalDiagram.CalculatePushingFactor(5.0);

        Assert.True(factor3_0 > 1.0, "Strong pushing factor at rho=3.0 must exceed 1.0");
        Assert.True(factor4_0 > factor3_0, "Pushing factor must strictly increase with density");
        Assert.True(factor5_0 > factor4_0, "Pushing factor at near-jam density must be high");

        double mildForce = PedestrianFundamentalDiagram.CalculateRearPushingForce(
            density: 1.50, distance: 0.72, radiusSum: 0.70, contactBuffer: 0.30, rearForwardDrive: 1.0);
        double strongForce = PedestrianFundamentalDiagram.CalculateRearPushingForce(
            density: 3.50, distance: 0.72, radiusSum: 0.70, contactBuffer: 0.30, rearForwardDrive: 1.0);

        Assert.True(strongForce > mildForce * 2.0,
            $"Strong pushing force ({strongForce:F2}) must be substantially higher than mild pushing ({mildForce:F2})");

        var state = PedestrianFundamentalDiagram.DeterminePushingState(3.50, headwayDistance: 0.60);
        Assert.Equal(PedestrianFundamentalDiagram.PushingBehaviorState.StrongPushing, state);
    }

    [Fact]
    public void FrontAgent_AcceleratesForward_WhenPushedFromRear()
    {
        // Physical verification: Agent 0 (front) is moving slowly at v = 0.20 m/s.
        // Agent 1 (rear) is at spacing d = 0.75m (in contact buffer) with forward drive of 1.4 m/s in crowd density rho = 2.0.
        double density = 2.0; // Regime II (Mild Pushing)
        double frontSpeed = 0.20;
        double rearDrive = 1.40;
        double distance = 0.75;
        double radiusSum = 0.70;

        double rearPush = PedestrianFundamentalDiagram.CalculateRearPushingForce(
            density, distance, radiusSum, contactBuffer: 0.30, rearForwardDrive: rearDrive);

        Assert.True(rearPush > 0.0, "Rear push force must be exerted on front agent");

        // Simulate 1 physics step (inertia = 0.25)
        double inertia = 0.25;
        double motiveGoalSpeed = 0.20; // Front agent desired slow speed

        double finalForce = motiveGoalSpeed + rearPush;
        double updatedSpeed = frontSpeed * (1.0 - inertia) + finalForce * inertia;

        // Front agent must accelerate forward due to the rear push
        Assert.True(updatedSpeed > frontSpeed,
            $"Front agent speed should increase from {frontSpeed} m/s due to rear push, got {updatedSpeed:F3} m/s");
    }

    [Fact]
    public void MultiAgentChain_PropagatesPushingWaveFromRearToFront()
    {
        // Model 3 agents in single-file column at X = 0.0 (Agent 0, front), X = -0.75m (Agent 1, middle), X = -1.50m (Agent 2, rear).
        // Crowd density rho = 3.0 (Regime III, Strong Pushing).
        // Rear agent presses forward with full speed (1.5 m/s).
        // The compressive pushing force must propagate from Agent 2 to Agent 1, and Agent 1 to Agent 0.
        double density = 3.0;
        double radiusSum = 0.70;
        double gap = 0.75;

        // Agent 2 pushes Agent 1
        double push2to1 = PedestrianFundamentalDiagram.CalculateRearPushingForce(
            density, gap, radiusSum, contactBuffer: 0.30, rearForwardDrive: 1.5);
        Assert.True(push2to1 > 0.0);

        // Agent 1 receives push2to1, so Agent 1's forward drive into Agent 0 is enhanced
        double agent1Drive = 1.0 + push2to1 * 0.5;
        double push1to0 = PedestrianFundamentalDiagram.CalculateRearPushingForce(
            density, gap, radiusSum, contactBuffer: 0.30, rearForwardDrive: agent1Drive);

        // Agent 0 at the head of the column receives substantial forward push
        Assert.True(push1to0 > push2to1,
            $"Forward pushing force should accumulate along the chain: push1to0 ({push1to0:F2}) > push2to1 ({push2to1:F2})");
    }
}
