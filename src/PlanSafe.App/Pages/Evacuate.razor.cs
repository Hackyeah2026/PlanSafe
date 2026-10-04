using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PlanSafe.App.Services;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using PlanSafe.Contracts.Simulation;

namespace PlanSafe.App.Pages;

public partial class Evacuate
{
    [SupplyParameterFromQuery(Name = "session")]
    public string? SessionId { get; set; }

    [SupplyParameterFromQuery(Name = "wdist")]
    public double? QueryWeightDistance { get; set; }

    [SupplyParameterFromQuery(Name = "wocc")]
    public double? QueryWeightOccupancy { get; set; }

    [SupplyParameterFromQuery(Name = "mode")]
    public string? QueryMode { get; set; }

    private bool detailsExpanded = true;
    private bool HasConfirmedLocation => HasValidLocation && !isProvisionalLocation;
    private bool CanNavigate => IsPlanReady && HasConfirmedLocation && assignment?.Target != null && !assignment.IsOutsideZone;
    private bool NeedsLocation => !HasConfirmedLocation || isPickingLocation;
    private bool locationFromGps;
    private string PanelTitle => isPickingLocation && !detailsExpanded
        ? (isOutsideZone ? L.EvacuationUx.PickInside : L.EvacuationUx.PickOnMap)
        : NeedsLocation ? L.EvacuationUx.ConfirmLocation : L.EvacuationUi.YourShelter;

    private Func<string>? planLoadError;
    private bool IsPlanReady => isEnvironmentLoaded && planLoadError == null;
    private bool isEnvironmentLoaded = false;
    private bool mapInitialized = false;
    private bool mapInitializing;
    private bool loading = true;
    private bool isGpsLoading = false;
    private bool isPickingLocation = false;
    private bool isOutsideZone = false;
    private bool isProvisionalLocation;
    private bool canRetryGps = true;

    private Func<string>? errorMessage;
    private Func<string>? gpsErrorMessage;

    private double? gpsLatitude;
    private double? gpsLongitude;
    private double? gpsAccuracy;

    private double weightDistance = 0.5;
    private double weightOccupancy = 0.5;

    private List<EvacuationTarget> targets = new();
    private List<List<GeoCoordinate>> roadblocks = new();
    private List<List<GeoCoordinate>> evacZones = new();
    private List<List<GeoCoordinate>> safeZones = new();

    private TargetAssignmentResponse? assignment;
    private string? currentAllocatedTargetId;
    private int _calculationSequence = 0;
    private int _manualLocationSequence;
    private bool HasValidLocation => gpsLatitude.HasValue && gpsLongitude.HasValue &&
        double.IsFinite(gpsLatitude.Value) && double.IsFinite(gpsLongitude.Value) &&
        evacZones.Any(zone => GeoMath.IsPointInPolygon(gpsLatitude.Value, gpsLongitude.Value, zone));

    private IJSObjectReference? citizenMapModule;
    private DotNetObjectReference<Evacuate>? dotNetRef;
    private System.Threading.Timer? pollTimer;

    private string GetApiBaseUrl()
    {
        return EvacuationApiUrlResolver.Resolve(Navigation.BaseUri);
    }

    private record GpsResult(
        bool Success,
        bool IsSecureContext,
        double? Latitude,
        double? Longitude,
        double? Accuracy,
        string? Code,
        string? Message);

    protected override async Task OnInitializedAsync()
    {
        dotNetRef = DotNetObjectReference.Create(this);

        if (QueryWeightDistance.HasValue) weightDistance = Math.Clamp(QueryWeightDistance.Value, 0.0, 1.0);
        if (QueryWeightOccupancy.HasValue) weightOccupancy = Math.Clamp(QueryWeightOccupancy.Value, 0.0, 1.0);

        await LoadEnvironmentAsync();
        isEnvironmentLoaded = true;
        if (!IsPlanReady) return;

        // Frame the zone while waiting for a confirmed location; do not assign from its center.
        TrySetProvisionalZoneLocation();
        await FetchAssignment(skipApiRefresh: false, expectedSeq: null, isManualRelocation: isProvisionalLocation);

        // In background, request actual GPS to refine if available
        _ = RequestGpsAndAssign(isUserGesture: false);

        pollTimer = new System.Threading.Timer(async _ =>
        {
            if (IsPlanReady && HasConfirmedLocation && !loading && !isPickingLocation)
            {
                await InvokeAsync(() => FetchAssignment(skipApiRefresh: false));
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (isEnvironmentLoaded && !mapInitialized && !mapInitializing)
        {
            // GPS and assignment responses can render again while the module import is pending.
            mapInitializing = true;
            try
            {
                await InitRealMapAsync();
            }
            finally
            {
                mapInitializing = false;
            }
        }

    }

    private async Task InitRealMapAsync()
    {
        try
        {
            citizenMapModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/citizenMapInterop.js");
            double centerLat = gpsLatitude ?? 50.0614;
            double centerLng = gpsLongitude ?? 19.9366;
            var ok = await citizenMapModule.InvokeAsync<bool>("initCitizenMap", "citizen-leaflet-map", centerLat, centerLng, 15, IsPlanReady ? dotNetRef : null, !IsPlanReady);
            if (ok)
            {
                mapInitialized = true;
                if (IsPlanReady) await UpdateCitizenMapJsAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Evacuate] InitRealMapAsync error: {ex.Message}");
        }
    }

    private async Task LoadEnvironmentAsync()
    {
        try
        {
            string url = $"{GetApiBaseUrl()}/api/evacuate/config";
            if (!string.IsNullOrEmpty(SessionId))
            {
                url += $"?session={Uri.EscapeDataString(SessionId)}";
            }

            var cfg = await Http.GetFromJsonAsync<EvacuationPlanConfig>(url);
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.SessionId))
            {
                planLoadError = () => L.EvacuationUi.LoadError;
                return;
            }
            if (!string.IsNullOrEmpty(SessionId) && !string.Equals(SessionId, cfg.SessionId, StringComparison.Ordinal))
            {
                planLoadError = () => L.EvacuationUi.WrongPlan;
                return;
            }
            if (cfg.ExpiresAtUtc.HasValue && cfg.ExpiresAtUtc.Value <= DateTime.UtcNow)
            {
                planLoadError = () => L.EvacuationUi.Expired;
                return;
            }

            targets = cfg.Targets?.ToList() ?? new();
            roadblocks = cfg.Roadblocks?.ToList() ?? new();
            evacZones = cfg.EvacuationZones?.ToList() ?? new();
            safeZones = cfg.SafeZones?.ToList() ?? new();
            if (!QueryWeightDistance.HasValue) weightDistance = cfg.WeightDistance;
            if (!QueryWeightOccupancy.HasValue) weightOccupancy = cfg.WeightOccupancy;
            SessionId = cfg.SessionId;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Evacuate] LoadEnvironment error: {ex.Message}");
            planLoadError = () => L.EvacuationUi.LoadError;
        }
    }

    private void ReloadPlan() => Navigation.Refresh(forceReload: true);

    private void SetGpsFailure(string? code)
    {
        canRetryGps = code is not ("INSECURE_CONTEXT" or "NOT_SUPPORTED");
        gpsErrorMessage = () => code switch
        {
            "PERMISSION_DENIED" => L.EvacuationUx.GpsDenied,
            "TIMEOUT" => L.EvacuationUx.GpsTimeout,
            "POSITION_UNAVAILABLE" => L.EvacuationUx.GpsUnavailable,
            "INSECURE_CONTEXT" => L.EvacuationUx.GpsInsecure,
            "NOT_SUPPORTED" => L.EvacuationUx.GpsUnsupported,
            _ => L.EvacuationUx.GpsError
        };
    }

    private async Task RequestGpsAndAssign(bool isUserGesture = false)
    {
        if (!IsPlanReady || isGpsLoading) return;
        isGpsLoading = true;
        if (isUserGesture)
        {
            StateHasChanged();
        }

        int locationSequence = _manualLocationSequence;
        try
        {
            var res = await JS.InvokeAsync<GpsResult?>("getGeolocation");
            // A manual selection made while GPS was pending takes precedence.
            if (locationSequence != _manualLocationSequence) return;
            if (res != null && res.Success && res.Latitude.HasValue && res.Longitude.HasValue)
            {
                int seq = ++_calculationSequence;
                ApplyGpsLocation(res.Latitude.Value, res.Longitude.Value, res.Accuracy);
                assignment = null;
                isOutsideZone = !HasConfirmedLocation;
                isPickingLocation = !HasConfirmedLocation;
                detailsExpanded = true;
                canRetryGps = true;
                gpsErrorMessage = null;
                loading = true;
                await UpdateCitizenMapJsAsync();
                StateHasChanged();
                await FetchAssignment(skipApiRefresh: false, expectedSeq: seq, isManualRelocation: isProvisionalLocation);
            }
            else
            {
                SetGpsFailure(res is { IsSecureContext: false } ? "INSECURE_CONTEXT" : res?.Code);
            }
        }
        catch (JSException ex)
        {
            Console.WriteLine($"[Evacuate] GPS error: {ex.Message}");
            if (locationSequence == _manualLocationSequence) SetGpsFailure(null);
        }
        finally
        {
            isGpsLoading = false;
            if (assignment == null && gpsLatitude.HasValue && gpsLongitude.HasValue)
            {
                await FetchAssignment(skipApiRefresh: false);
            }
            StateHasChanged();
        }
    }

    private void ApplyGpsLocation(double latitude, double longitude, double? accuracy)
    {
        gpsLatitude = Math.Round(latitude, 6);
        gpsLongitude = Math.Round(longitude, 6);
        gpsAccuracy = accuracy;
        locationFromGps = true;
        isProvisionalLocation = false;

        if (!HasValidLocation && double.IsFinite(latitude) && double.IsFinite(longitude))
        {
            TrySetProvisionalZoneLocation();
        }
    }

    private bool TrySetProvisionalZoneLocation()
    {
        // Use an interior point of the first configured evacuation zone.
        var zone = evacZones.FirstOrDefault(z => z.Count >= 3);
        var center = zone == null ? null : GeoMath.GetInteriorCenter(zone);
        if (center is not { } location) return false;

        gpsLatitude = location.Latitude;
        gpsLongitude = location.Longitude;
        gpsAccuracy = 0; // A provisional point has no measured GPS accuracy.
        isProvisionalLocation = true;
        return true;
    }

    private void TogglePanel()
    {
        detailsExpanded = !detailsExpanded;
    }

    private void ShowLocationOptions()
    {
        isPickingLocation = true;
        detailsExpanded = true;
    }

    private Task EnableMapLocationCorrection()
    {
        ++_manualLocationSequence;
        isPickingLocation = true;
        detailsExpanded = false;
        gpsErrorMessage = null;
        return Task.CompletedTask;
    }

    private void CancelLocationCorrection()
    {
        ++_manualLocationSequence;
        isPickingLocation = false;
        detailsExpanded = true;
    }

    [JSInvokable]
    public async Task OnCitizenMapClicked(double lat, double lng)
    {
        if (!IsPlanReady) return;
        ++_manualLocationSequence;
        int seq = ++_calculationSequence;
        gpsLatitude = Math.Round(lat, 6);
        gpsLongitude = Math.Round(lng, 6);
        gpsAccuracy = null;
        locationFromGps = false;
        isProvisionalLocation = false;
        detailsExpanded = HasValidLocation;
        canRetryGps = true;
        gpsErrorMessage = null;
        isPickingLocation = !HasValidLocation;
        isOutsideZone = !HasValidLocation;
        assignment = null;
        loading = true;
        await UpdateCitizenMapJsAsync();
        StateHasChanged();
        await FetchAssignment(skipApiRefresh: false, expectedSeq: seq, isManualRelocation: true);
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnShelterSelected(string shelterId)
    {
        var target = targets.FirstOrDefault(t => t.Id == shelterId);
        if (!IsPlanReady || target == null) return;
        if (isPickingLocation && !detailsExpanded)
        {
            await OnCitizenMapClicked(target.Latitude ?? target.Y, target.Longitude ?? target.X);
            return;
        }
        if (!HasConfirmedLocation || !gpsLatitude.HasValue || !gpsLongitude.HasValue) return;

        int seq = ++_calculationSequence;
        loading = true;
        currentAllocatedTargetId = target.Id;
        StateHasChanged();

        try
        {
            var req = new TargetAssignmentRequest(
                X: gpsLongitude.Value,
                Y: gpsLatitude.Value,
                WeightDistance: weightDistance,
                WeightOccupancy: weightOccupancy,
                CustomTargets: new List<EvacuationTarget> { target },
                Latitude: gpsLatitude.Value,
                Longitude: gpsLongitude.Value,
                SessionId: SessionId,
                CurrentTargetId: currentAllocatedTargetId,
                IgnoreHysteresis: true,
                StrictGeofence: true
            );

            TargetAssignmentResponse? assignResult = null;
            try
            {
                var response = await Http.PostAsJsonAsync($"{GetApiBaseUrl()}/api/evacuate/assign", req);
                if (response.IsSuccessStatusCode)
                {
                    assignResult = await response.Content.ReadFromJsonAsync<TargetAssignmentResponse>();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Evacuate] API assign on shelter selected failed: {ex.Message}");
            }

            if (assignResult == null)
            {
                assignResult = TargetSelector.SelectGeoTarget(
                    citizenLat: gpsLatitude.Value,
                    citizenLng: gpsLongitude.Value,
                    targets: new List<EvacuationTarget> { target },
                    weightDistance: weightDistance,
                    weightOccupancy: weightOccupancy,
                    roadblocks: roadblocks,
                    evacuationZones: evacZones,
                    currentTargetId: currentAllocatedTargetId,
                    ignoreHysteresis: true,
                    strictGeofence: true
                );
            }

            if (seq == _calculationSequence && assignResult != null)
            {
                assignment = assignResult;
                isOutsideZone = assignResult.IsOutsideZone;
                errorMessage = null;

                _ = Http.PostAsJsonAsync($"{GetApiBaseUrl()}/api/evacuate/checkin", new CheckInRequest(currentAllocatedTargetId, 1));

                await UpdateCitizenMapJsAsync();
            }
        }
        catch (Exception ex)
        {
            errorMessage = () => L.Common.ErrorDetails(ex.Message);
        }
        finally
        {
            if (seq == _calculationSequence)
            {
                loading = false;
                StateHasChanged();
            }
        }
    }

    private Task FetchAssignment(bool skipApiRefresh) => FetchAssignment(skipApiRefresh, expectedSeq: null, isManualRelocation: false);

    private async Task FetchAssignment(bool skipApiRefresh, int? expectedSeq, bool isManualRelocation = false)
    {
        if (!IsPlanReady) return;
        if (isProvisionalLocation)
        {
            loading = false;
            return;
        }
        int seq = expectedSeq ?? ++_calculationSequence;
        if (!HasValidLocation || !gpsLatitude.HasValue || !gpsLongitude.HasValue)
        {
            assignment = null;
            isOutsideZone = gpsLatitude.HasValue && gpsLongitude.HasValue;
            isPickingLocation = true;
            loading = false;
            await UpdateCitizenMapJsAsync();
            StateHasChanged();
            return;
        }
        isOutsideZone = false;

        try
        {
            var req = new TargetAssignmentRequest(
                X: gpsLongitude.Value,
                Y: gpsLatitude.Value,
                WeightDistance: weightDistance,
                WeightOccupancy: weightOccupancy,
                Latitude: gpsLatitude.Value,
                Longitude: gpsLongitude.Value,
                SessionId: SessionId,
                CurrentTargetId: currentAllocatedTargetId,
                IgnoreHysteresis: isManualRelocation,
                StrictGeofence: true
            );

            TargetAssignmentResponse? assignResult = null;
            try
            {
                var response = await Http.PostAsJsonAsync($"{GetApiBaseUrl()}/api/evacuate/assign", req);
                if (response.IsSuccessStatusCode)
                {
                    assignResult = await response.Content.ReadFromJsonAsync<TargetAssignmentResponse>();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Evacuate] API assign failed, falling back to local calculation: {ex.Message}");
            }

            // Local fallback calculation if API was unreachable or returned an error
            if (assignResult == null && targets.Count > 0)
            {
                assignResult = TargetSelector.SelectGeoTarget(
                    citizenLat: gpsLatitude.Value,
                    citizenLng: gpsLongitude.Value,
                    targets: targets,
                    weightDistance: weightDistance,
                    weightOccupancy: weightOccupancy,
                    roadblocks: roadblocks,
                    evacuationZones: evacZones,
                    currentTargetId: currentAllocatedTargetId,
                    ignoreHysteresis: isManualRelocation,
                    strictGeofence: true
                );
            }

            if (seq == _calculationSequence && assignResult != null)
            {
                assignment = assignResult;
                isOutsideZone = assignResult.IsOutsideZone;
                errorMessage = null;

                // Slot balancing: if target changed, balance occupancy
                if (assignResult.Target != null && assignResult.Target.Id != currentAllocatedTargetId)
                {
                    var prevTarget = currentAllocatedTargetId;
                    currentAllocatedTargetId = assignResult.Target.Id;

                    if (!string.IsNullOrEmpty(prevTarget))
                    {
                        _ = Http.PostAsJsonAsync($"{GetApiBaseUrl()}/api/evacuate/checkin", new CheckInRequest(prevTarget, -1));
                    }
                    _ = Http.PostAsJsonAsync($"{GetApiBaseUrl()}/api/evacuate/checkin", new CheckInRequest(currentAllocatedTargetId, 1));
                }

                await UpdateCitizenMapJsAsync();
            }
        }
        catch (Exception ex)
        {
            errorMessage = () => L.Common.CommunicationError(ex.Message);
        }
        finally
        {
            if (seq == _calculationSequence)
            {
                loading = false;
                StateHasChanged();
            }
        }
    }

    private static readonly JsonSerializerOptions _camelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private async Task UpdateCitizenMapJsAsync()
    {
        if (citizenMapModule != null)
        {
            try
            {
                string sheltersJson = JsonSerializer.Serialize(targets, _camelCaseJsonOptions);
                string? selectedId = assignment?.Target?.Id;
                string roadblocksJson = JsonSerializer.Serialize(roadblocks, _camelCaseJsonOptions);
                var routeCoords = assignment?.RoutePath?
                    .Select(p => new[] { p.Latitude, p.Longitude })
                    .ToList() ?? new List<double[]>();
                string routeJson = JsonSerializer.Serialize(routeCoords);
                string zonesJson = JsonSerializer.Serialize(evacZones, _camelCaseJsonOptions);
                string safeZonesJson = JsonSerializer.Serialize(safeZones, _camelCaseJsonOptions);

                await citizenMapModule.InvokeVoidAsync(
                    "updateCitizenMap",
                    "citizen-leaflet-map",
                    isProvisionalLocation ? null : gpsLatitude,
                    isProvisionalLocation ? null : gpsLongitude,
                    gpsAccuracy ?? 0.0,
                    sheltersJson,
                    selectedId,
                    roadblocksJson,
                    routeJson,
                    zonesJson,
                    safeZonesJson,
                    isProvisionalLocation || !HasValidLocation
                );
            }
            catch
            {
            }
        }
    }

    private async Task FitMapBoundsAsync()
    {
        if (citizenMapModule != null)
        {
            try
            {
                await citizenMapModule.InvokeVoidAsync("fitCitizenBounds", "citizen-leaflet-map");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Evacuate] FitMapBounds error: {ex.Message}");
            }
        }
    }

    private string? GetMapsUrl(EvacuationTarget target)
    {
        if (!HasConfirmedLocation || assignment?.IsOutsideZone != false) return null;
        double destLat = target.Latitude ?? target.Y;
        double destLng = target.Longitude ?? target.X;
        GeoCoordinate? origin = gpsLatitude.HasValue && gpsLongitude.HasValue
            ? new GeoCoordinate(gpsLatitude.Value, gpsLongitude.Value)
            : null;
        return GoogleMapsUrlBuilder.Build(new GeoCoordinate(destLat, destLng), origin, assignment?.RoutePath);
    }

    public async ValueTask DisposeAsync()
    {
        if (!string.IsNullOrEmpty(currentAllocatedTargetId))
        {
            var targetToRelease = currentAllocatedTargetId;
            currentAllocatedTargetId = null;
            try
            {
                await Http.PostAsJsonAsync($"{GetApiBaseUrl()}/api/evacuate/checkin", new CheckInRequest(targetToRelease, -1));
            }
            catch
            {
            }
        }

        pollTimer?.Dispose();

        if (citizenMapModule != null)
        {
            try
            {
                await citizenMapModule.InvokeVoidAsync("disposeCitizenMap", "citizen-leaflet-map");
                await citizenMapModule.DisposeAsync();
            }
            catch
            {
            }
            citizenMapModule = null;
        }

        dotNetRef?.Dispose();
    }
}
