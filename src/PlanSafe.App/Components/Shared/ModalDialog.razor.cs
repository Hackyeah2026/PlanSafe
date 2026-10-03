using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PlanSafe.App.Components.Shared;

public partial class ModalDialog : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Parameter] public bool IsOpen { get; set; }
    [Parameter] public string Width { get; set; } = "820px";
    [Parameter] public string? Class { get; set; }
    [Parameter, EditorRequired] public string LabelledBy { get; set; } = string.Empty;
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private ElementReference dialog;
    private IJSObjectReference? module;
    private bool mounted;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (IsOpen == mounted) return;
        module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/dialogs.js");
        await module.InvokeVoidAsync(IsOpen ? "mount" : "unmount", dialog);
        mounted = IsOpen;
    }

    public async ValueTask DisposeAsync()
    {
        if (module is null) return;
        try
        {
            if (mounted) await module.InvokeVoidAsync("unmount", dialog);
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
    }
}
