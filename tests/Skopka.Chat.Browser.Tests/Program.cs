using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Skopka.Chat.Browser.Testing;
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Logging.ClearProviders();
builder.RootComponents.Add<App>("#app");
await builder.Build().RunAsync();
