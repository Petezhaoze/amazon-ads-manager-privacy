# Amazon Ads Manager

A private dayparting app for Amazon Advertising — for Peter and Dad's accounts.

## What it does

Toggles Amazon Sponsored Products campaigns between `enabled` and `paused` based on a weekly hour-by-hour schedule (dayparting). A GitHub Actions workflow calls `/api/run-schedule` hourly; the backend checks each campaign's schedule and flips its state if needed.

## Tech Stack

- **Frontend**: Blazor WebAssembly (.NET 8)
- **Backend**: Azure Functions isolated worker (.NET 8)
- **Hosting**: Azure Static Web Apps (Free Tier)
- **CI/CD**: GitHub Actions

---

## Local Development

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local)
- [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite) (local storage emulator)

### 1. Clone & Configure

```bash
git clone https://github.com/YOUR_USERNAME/amazon-ads-manager.git
cd amazon-ads-manager
```

Create `src/AmazonAdsManager.Api/local.settings.json` (already in `.gitignore`):

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AmazonAds:ClientId": "YOUR_CLIENT_ID",
    "AmazonAds:ClientSecret": "YOUR_CLIENT_SECRET",
    "AmazonAds:Accounts:0:AccountKey": "peter",
    "AmazonAds:Accounts:0:DisplayName": "Peter",
    "AmazonAds:Accounts:0:RefreshToken": "YOUR_REFRESH_TOKEN",
    "AmazonAds:Accounts:0:ProfileId": "YOUR_PROFILE_ID",
    "AmazonAds:Accounts:0:BaseUrl": "https://advertising-api.amazon.com",
    "AmazonAds:Accounts:1:AccountKey": "dad",
    "AmazonAds:Accounts:1:DisplayName": "Dad",
    "AmazonAds:Accounts:1:RefreshToken": "DADS_REFRESH_TOKEN",
    "AmazonAds:Accounts:1:ProfileId": "DADS_PROFILE_ID",
    "AmazonAds:Accounts:1:BaseUrl": "https://advertising-api.amazon.com",
    "RunnerKey": "change-me-secret-runner-key"
  }
}
```

### 2. Run the API

```bash
cd src/AmazonAdsManager.Api
func start
# API runs on http://localhost:7071
```

### 3. Run the Blazor Client

In a second terminal:

```bash
cd src/AmazonAdsManager.Client
dotnet run
# Opens on https://localhost:5001 (or similar)
```

For local dev the client's `wwwroot/appsettings.json` points `/api` to the Static Web Apps proxy. Override by adding `wwwroot/appsettings.Development.json`:

```json
{ "ApiBaseUrl": "http://localhost:7071/api" }
```

---

## Azure Deployment

### 1. Create Azure Static Web App

- Portal → Create resource → Static Web App
- Connect to your GitHub repo
- App location: `src/AmazonAdsManager.Client`
- API location: `src/AmazonAdsManager.Api`
- Output location: `wwwroot`

Azure will create a `AZURE_STATIC_WEB_APPS_API_TOKEN` secret automatically in your GitHub repo.

### 2. Add GitHub Secrets

| Secret | Value |
|--------|-------|
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | Auto-added by Azure |
| `RUNNER_KEY` | A random secret key for `/api/run-schedule` |
| `API_BASE_URL` | Your Static Web App URL, e.g. `https://xxx.azurestaticapps.net` |

### 3. Configure Azure App Settings

In the Azure portal → your Static Web App → Configuration, add all the `AmazonAds:*` keys from `local.settings.json` plus `RunnerKey`.

### 4. Push to main

The `azure-static-web-apps.yml` workflow deploys automatically on push to `main`.

---

## Security Notes

- `local.settings.json` is in `.gitignore` — **never commit it**
- The Blazor client never receives `ClientSecret` or `RefreshToken` — only `AccountKey` and `DisplayName` are exposed via `GET /api/accounts`
- `/api/run-schedule` is protected by the `x-runner-key` header

## Project Structure

```
src/
  AmazonAdsManager.Client/    # Blazor WebAssembly
  AmazonAdsManager.Api/       # Azure Functions backend
  AmazonAdsManager.Shared/    # Shared models (DTOs, options)
.github/workflows/
  azure-static-web-apps.yml   # Deploy on push to main
  run-schedule.yml             # Hourly schedule runner
staticwebapp.config.json       # SWA routing config
```
