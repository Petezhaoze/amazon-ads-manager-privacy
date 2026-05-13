using AmazonAdsManager.Client;
using AmazonAdsManager.Client.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBase = builder.Configuration["ApiBaseUrl"]
    ?? builder.HostEnvironment.BaseAddress.TrimEnd('/') + "/api";

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });
builder.Services.AddScoped<AdsApiClient>();
builder.Services.AddScoped<AccountState>();
builder.Services.AddScoped<AppPreferencesService>();

await builder.Build().RunAsync();
