using Microsoft.AspNetCore.Components;
using PlanSafe.Contracts.Models.Session;

namespace PlanSafe.App.Components;

public partial class CreateBranchModal
{
    [Parameter] public bool IsOpen { get; set; }
    [Parameter] public MapSession? ParentSession { get; set; }
    [Parameter] public EventCallback<SessionBranchRequest> OnBranchCreated { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private string _branchName = string.Empty;
    private string? _description;
    private bool _copyItems = true;
    private bool _isSubmitting = false;
    private Func<string?>? _errorMessage;

    protected override void OnParametersSet()
    {
        if (IsOpen && string.IsNullOrWhiteSpace(_branchName) && ParentSession != null)
        {
            int rnd = new Random().Next(100, 999);
            _branchName = $"scenario/branch-{rnd}";
            _description = null;
            _copyItems = true;
            _errorMessage = null;
        }
    }

    private async Task HandleCreate()
    {
        if (ParentSession == null) return;

        if (string.IsNullOrWhiteSpace(_branchName))
        {
            _errorMessage = () => L.Scenario.NameRequired;
            return;
        }

        _errorMessage = null;
        _isSubmitting = true;

        var request = new SessionBranchRequest
        {
            ParentSessionId = ParentSession.Id,
            BranchName = _branchName.Trim(),
            Description = _description?.Trim(),
            CopyItems = _copyItems
        };

        try
        {
            if (OnBranchCreated.HasDelegate)
            {
                await OnBranchCreated.InvokeAsync(request);
            }
            await Close();
        }
        catch (Exception ex)
        {
            _errorMessage = () => ex.Message;
        }
        finally
        {
            _isSubmitting = false;
        }
    }

    private Task Close()
    {
        _branchName = string.Empty;
        _description = null;
        _errorMessage = null;
        return OnClose.InvokeAsync();
    }
}
