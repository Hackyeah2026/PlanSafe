using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PlanSafe.App;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
await builder.Build().RunAsync();
