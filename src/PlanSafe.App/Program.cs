using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PlanSafe.App;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<PlanSafe.App.Services.IMapInterop, PlanSafe.App.Services.MapInterop>();
builder.Services.AddScoped<PlanSafe.App.Services.IMapSessionService, PlanSafe.App.Services.MapSessionService>();
builder.Services.AddScoped<PlanSafe.App.Services.Gus.IGusCensusService, PlanSafe.App.Services.Gus.GusCensusService>();
builder.Services.AddScoped<PlanSafe.App.Services.Gus.IGusOccupantGenerator, PlanSafe.App.Services.Gus.GusOccupantGenerator>();
builder.Services.AddScoped<PlanSafe.App.Services.Osm.IOsmObstacleService, PlanSafe.App.Services.Osm.OsmObstacleService>();

await builder.Build().RunAsync();
