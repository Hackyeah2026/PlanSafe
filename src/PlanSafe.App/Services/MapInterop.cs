using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace PlanSafe.App.Services
{
    public interface IMapInterop : IAsyncDisposable
    {
        Task<bool> InitializeMapAsync(string containerId, double? lat = null, double? lng = null, int? zoom = null);
        Task InvalidateSizeAsync(string containerId);
        Task CenterOnKrakowAsync(string containerId);
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

        public async Task<bool> InitializeMapAsync(string containerId, double? lat = null, double? lng = null, int? zoom = null)
        {
            var module = await _moduleTask.Value;
            var options = new
            {
                lat = lat ?? 50.0614,
                lng = lng ?? 19.9366,
                zoom = zoom ?? 14
            };
            return await module.InvokeAsync<bool>("initMap", containerId, options);
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
                // Component disposed or page navigated away
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
                // Component disposed or page navigated away
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
                    // Component disposed
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
                    // Circuit closed
                }
            }
        }
    }
}
