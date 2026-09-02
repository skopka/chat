using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Skopka.Chat.Browser.Sample;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.Logging.ClearProviders();
await builder.Build().RunAsync();
