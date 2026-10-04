using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace PlanSafe.App.Localization;

public abstract class PlanSafeComponentBase : ComponentBase
{
    [Inject] private IStringLocalizer<UiStrings> Strings { get; set; } = default!;
    [Inject] private LanguageService Languages { get; set; } = default!;

    private LocalizedText? _text;

    protected LocalizedText L
    {
        get
        {
            // Simulation loops can retain the culture of their original async context.
            Languages.ApplyCurrentCulture();
            return _text ??= new LocalizedText(Strings);
        }
    }

    // A cascading value change rerenders existing components without losing their state.
    [CascadingParameter(Name = "UiCulture")] public string? UiCulture { get; set; }
}
