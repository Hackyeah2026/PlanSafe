using PlanSafe.App.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class WeidmannModelTests
{
    [Fact]
    public void CalculateSpeed_ZeroOrNegativeDensity_ReturnsFreeSpeed()
    {
        float speedZero = WeidmannModel.CalculateSpeed(0f);
        float speedNeg = WeidmannModel.CalculateSpeed(-1f);

        Assert.Equal(WeidmannModel.DefaultFreeSpeed, speedZero);
        Assert.Equal(WeidmannModel.DefaultFreeSpeed, speedNeg);
    }

    [Fact]
    public void CalculateSpeed_JamDensityOrAbove_ReturnsZero()
    {
        float speedJam = WeidmannModel.CalculateSpeed(WeidmannModel.DefaultJamDensity);
        float speedOverJam = WeidmannModel.CalculateSpeed(WeidmannModel.DefaultJamDensity + 2.0f);

        Assert.Equal(0f, speedJam);
        Assert.Equal(0f, speedOverJam);
    }

    [Fact]
    public void CalculateSpeed_IsMonotonicallyDecreasing_AcrossDensityRange()
    {
        float prevSpeed = WeidmannModel.CalculateSpeed(0f);

        for (float rho = 0.1f; rho <= WeidmannModel.DefaultJamDensity; rho += 0.1f)
        {
            float currentSpeed = WeidmannModel.CalculateSpeed(rho);
            Assert.True(currentSpeed <= prevSpeed, $"Speed at density {rho} ({currentSpeed}) should be <= previous speed ({prevSpeed})");
            Assert.True(currentSpeed >= 0f, "Speed must be non-negative");

            // For densities with noticeable crowd interaction (rho >= 0.5), speed drops strictly
            if (rho >= 0.6f)
            {
                Assert.True(currentSpeed < prevSpeed, $"Speed at density {rho} ({currentSpeed}) should be strictly less than previous speed ({prevSpeed})");
            }

            prevSpeed = currentSpeed;
        }
    }

    [Theory]
    [InlineData(1.0f, 1.058f, 0.05f)]
    [InlineData(2.0f, 0.606f, 0.05f)]
    [InlineData(4.0f, 0.156f, 0.05f)]
    public void CalculateSpeed_MatchesEmpiricalReferencePoints(float density, float expectedSpeed, float tolerance)
    {
        float actualSpeed = WeidmannModel.CalculateSpeed(density);
        Assert.InRange(actualSpeed, expectedSpeed - tolerance, expectedSpeed + tolerance);
    }
}
