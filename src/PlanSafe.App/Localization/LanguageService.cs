using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace PlanSafe.App.Localization;

public sealed class LanguageService(IJSRuntime js, IStringLocalizer<UiStrings> strings)
{
    public string Language { get; private set; } = "en";
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        var language = await js.InvokeAsync<string>("planSafeLocalization.detect");
        await ApplyAsync(language, false);
    }

    public Task SelectAsync(string language) => ApplyAsync(language, true);

    public void ApplyCurrentCulture()
    {
        var culture = CultureInfo.GetCultureInfo(Language == "pl" ? "pl-PL" : "en-GB");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private async Task ApplyAsync(string language, bool persist)
    {
        Language = language == "pl" ? "pl" : "en";
        ApplyCurrentCulture();
        var catalogue = strings.GetAllStrings(includeParentCultures: true)
            .ToDictionary(value => value.Name, value => value.Value);
        await js.InvokeVoidAsync("planSafeLocalization.apply", Language, catalogue, persist);
        Changed?.Invoke();
    }
}
