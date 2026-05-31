using AmazonAdsManager.Client;
using AmazonAdsManager.Client.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var configuredPath = builder.Configuration["ApiBaseUrl"] ?? "/api";
var apiBase = configuredPath.StartsWith("http")
    ? configuredPath
    : new Uri(new Uri(builder.HostEnvironment.BaseAddress), configuredPath).ToString();
if (!apiBase.EndsWith("/")) apiBase += "/";

builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(apiBase),
    Timeout = TimeSpan.FromMinutes(6)
});
builder.Services.AddScoped<AdsApiClient>();
builder.Services.AddScoped<AccountState>();
builder.Services.AddScoped<AppPreferencesService>();
builder.Services.AddScoped<ApiAccessTokenStore>();

await builder.Build().RunAsync();
