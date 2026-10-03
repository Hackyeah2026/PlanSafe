using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace PlanSafe.App.Services
{
    public interface IMapInterop : IAsyncDisposable
    {
        Task<bool> InitializeMapAsync(string containerId, double? lat = null, double? lng = null, int? zoom = null, object? dotNetRef = null);
        Task InvalidateSizeAsync(string containerId);
        Task CenterOnKrakowAsync(string containerId);
        Task SetDrawModeAsync(string containerId, string mode);
        Task FinishPolygonAsync(string containerId);
        Task CancelCurrentDrawingAsync(string containerId);
        Task DeleteMapItemAsync(string containerId, string itemId);
        Task ClearAllMapItemsAsync(string containerId);
        Task PanToMapItemAsync(string containerId, string itemId);
        Task LoadSessionItemsAsync(string containerId, System.Collections.Generic.IEnumerable<PlanSafe.Contracts.Models.Map.MapZoneItem> items, double? lat = null, double? lng = null, int? zoom = null);
        Task RenderGusGridAsync(string containerId, System.Collections.Generic.IEnumerable<PlanSafe.Contracts.Models.Gus.GusGridCell> cells);
        Task ClearGusGridAsync(string containerId);
        Task RenderOccupantsAsync(string containerId, System.Collections.Generic.IEnumerable<PlanSafe.Contracts.Models.Gus.OccupantAgent> agents);
        Task ClearOccupantsAsync(string containerId);
        Task DisposeMapAsync(string containerId);
    }

    public class MapInterop : IMapInterop
    {
        private readonly IJSRuntime _jsRuntime;
        private readonly Lazy<Task<IJSObjectReference>> _moduleTask;

        public MapInterop(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
            _moduleTask = new Lazy<Task<IJSObjectReference>>(() =>
                _jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/mapInterop.js").AsTask());
        }

        public async Task<bool> InitializeMapAsync(string containerId, double? lat = null, double? lng = null, int? zoom = null, object? dotNetRef = null)
        {
            var module = await _moduleTask.Value;
            var options = new
            {
                lat = lat ?? 50.0614,
                lng = lng ?? 19.9366,
                zoom = zoom ?? 14
            };
            return await module.InvokeAsync<bool>("initMap", containerId, options, dotNetRef);
        }

        public async Task SetDrawModeAsync(string containerId, string mode)
        {
            try
            {
                var module = await _moduleTask.Value;
                await module.InvokeVoidAsync("setDrawMode", containerId, mode);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task FinishPolygonAsync(string containerId)
        {
            try
            {
                var module = await _moduleTask.Value;
                await module.InvokeVoidAsync("finishPolygon", containerId);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task CancelCurrentDrawingAsync(string containerId)
        {
            try
            {
                var module = await _moduleTask.Value;
                await module.InvokeVoidAsync("cancelCurrentDrawing", containerId);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task DeleteMapItemAsync(string containerId, string itemId)
        {
            try
            {
                var module = await _moduleTask.Value;
                await module.InvokeVoidAsync("deleteMapItem", containerId, itemId);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task ClearAllMapItemsAsync(string containerId)
        {
            try
            {
                var module = await _moduleTask.Value;
                await module.InvokeVoidAsync("clearAllMapItems", containerId);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task PanToMapItemAsync(string containerId, string itemId)
        {
            try
            {
                var module = await _moduleTask.Value;
                await module.InvokeVoidAsync("panToMapItem", containerId, itemId);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task LoadSessionItemsAsync(string containerId, System.Collections.Generic.IEnumerable<PlanSafe.Contracts.Models.Map.MapZoneItem> items, double? lat = null, double? lng = null, int? zoom = null)
        {
            try
            {
                var module = await _moduleTask.Value;
                var itemsJson = System.Text.Json.JsonSerializer.Serialize(items);
                var options = new
                {
                    lat = lat,
                    lng = lng,
                    zoom = zoom
                };
                await module.InvokeVoidAsync("loadSessionItems", containerId, itemsJson, options);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task InvalidateSizeAsync(string containerId)
        {
            try
            {
                var module = await _moduleTask.Value;
                await module.InvokeVoidAsync("invalidateSize", containerId);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task CenterOnKrakowAsync(string containerId)
        {
            try
            {
                var module = await _moduleTask.Value;
                await module.InvokeVoidAsync("centerOnKrakow", containerId);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task RenderGusGridAsync(string containerId, System.Collections.Generic.IEnumerable<PlanSafe.Contracts.Models.Gus.GusGridCell> cells)
        {
            try
            {
                var module = await _moduleTask.Value;
                await module.InvokeVoidAsync("renderGusGrid", containerId, cells);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task ClearGusGridAsync(string containerId)
        {
            try
            {
                var module = await _moduleTask.Value;
                await module.InvokeVoidAsync("clearGusGrid", containerId);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task RenderOccupantsAsync(string containerId, System.Collections.Generic.IEnumerable<PlanSafe.Contracts.Models.Gus.OccupantAgent> agents)
        {
            try
            {
                var module = await _moduleTask.Value;
                await module.InvokeVoidAsync("renderOccupants", containerId, agents);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task ClearOccupantsAsync(string containerId)
        {
            try
            {
                var module = await _moduleTask.Value;
                await module.InvokeVoidAsync("clearOccupants", containerId);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        public async Task DisposeMapAsync(string containerId)
        {
            if (_moduleTask.IsValueCreated)
            {
                try
                {
                    var module = await _moduleTask.Value;
                    await module.InvokeVoidAsync("disposeMap", containerId);
                }
                catch (JSDisconnectedException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_moduleTask.IsValueCreated)
            {
                try
                {
                    var module = await _moduleTask.Value;
                    await module.DisposeAsync();
                }
                catch (JSDisconnectedException)
                {
                }
            }
        }
    }
}
