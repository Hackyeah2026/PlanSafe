using PlanSafe.App.Services;
using Xunit;

namespace PlanSafe.Tests;

public class EvacuationApiUrlResolverTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5050/", "http://127.0.0.1:5051")]
    [InlineData("http://localhost:5050/", "http://localhost:5051")]
    [InlineData("http://192.168.1.10:5050/", "http://192.168.1.10:5051")]
    [InlineData("http://[::1]:5050/", "http://[::1]:5051")]
    [InlineData("http://localhost:5171/", "http://localhost:49492")]
    [InlineData("https://localhost:7030/", "https://localhost:49491")]
    [InlineData("https://plansafe.example/", "https://plansafe.example")]
    [InlineData("https://plansafe.example/plans/", "https://plansafe.example/plans")]
    [InlineData("http://localhost:8080/", "http://localhost:8080")]
    public void ResolvesApiWithoutChangingClientAddress(string clientBaseUrl, string expected)
    {
        Assert.Equal(expected, EvacuationApiUrlResolver.Resolve(clientBaseUrl));
    }
}
