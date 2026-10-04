using Microsoft.AspNetCore.Components;
using PlanSafe.Contracts.Models.Session;

namespace PlanSafe.App.Components;

public partial class SimulationConfigModal : ComponentBase
{
    [Parameter] public bool IsOpen { get; set; }
    [Parameter] public SimulationConfig Config { get; set; } = new();
    [Parameter] public int CensusPopulation { get; set; } = 0;
    [Parameter] public EventCallback<SimulationConfig> OnSave { get; set; }
    [Parameter] public EventCallback OnStartSimulation { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private SimulationConfig _localConfig = new();
    private bool _wasOpen;

    protected override void OnParametersSet()
    {
        if (IsOpen && !_wasOpen)
        {
            _localConfig = Config.Clone();
        }
        _wasOpen = IsOpen;
    }

    private void SetCensusMode(bool useGus)
    {
        _localConfig.UseGusCensus = useGus;
        if (useGus && CensusPopulation > 0)
        {
            _localConfig.AgentCount = CensusPopulation;
        }
    }

    private void SetScenario(double wDist, double wOcc)
    {
        _localConfig.WeightDistance = Math.Clamp(Math.Round(wDist, 2), 0.0, 1.0);
        _localConfig.WeightOccupancy = Math.Round(1.0 - _localConfig.WeightDistance, 2);
    }

    private void OnWeightDistInput(ChangeEventArgs e)
    {
        if (double.TryParse(e.Value?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val))
        {
            _localConfig.WeightDistance = Math.Clamp(Math.Round(val, 2), 0.0, 1.0);
            _localConfig.WeightOccupancy = Math.Round(1.0 - _localConfig.WeightDistance, 2);
        }
    }

    private void OnWeightOccInput(ChangeEventArgs e)
    {
        if (double.TryParse(e.Value?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val))
        {
            _localConfig.WeightOccupancy = Math.Clamp(Math.Round(val, 2), 0.0, 1.0);
            _localConfig.WeightDistance = Math.Round(1.0 - _localConfig.WeightOccupancy, 2);
        }
    }

    private void ResetToDefaults()
    {
        _localConfig = new SimulationConfig
        {
            UseGusCensus = true,
            AgentCount = CensusPopulation > 0 ? CensusPopulation : 400,
            Granulation = 1,
            TimeScale = 1.0f,
            Unlimited = false,
            SocialRepulsionWeight = 4.5,
            WhiskerLength = 2.5,
            ShowWhiskers = false,
            RenderMode = "agents",
            RenderFps = "30",
            WeightDistance = 0.5,
            WeightOccupancy = 0.5
        };
    }

    private async Task SaveAndClose()
    {
        if (OnSave.HasDelegate)
        {
            await OnSave.InvokeAsync(_localConfig);
        }
        await Close();
    }

    private async Task SaveAndStart()
    {
        if (OnSave.HasDelegate)
        {
            await OnSave.InvokeAsync(_localConfig);
        }
        await Close();
        if (OnStartSimulation.HasDelegate)
        {
            await OnStartSimulation.InvokeAsync();
        }
    }

    private async Task Close()
    {
        if (OnClose.HasDelegate)
        {
            await OnClose.InvokeAsync();
        }
    }
}
