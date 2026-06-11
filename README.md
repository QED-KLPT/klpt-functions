# KLPT Functions

## Protected video access

`POST /api/videos/access` validates a shared passkey and returns a short-lived,
read-only SAS URL for an allow-listed video.

Example request:

```json
{
  "videoId": "sample-video",
  "passkey": "the-shared-passkey"
}
```

Example response:

```json
{
  "url": "https://example.blob.core.windows.net/klpt-videos/sample-video.mp4?...",
  "expiresAt": "2026-06-11T03:00:00+00:00"
}
```

Configure these application settings locally and in the Azure Function App:

| Setting | Example | Purpose |
| --- | --- | --- |
| `VideoAccess__Passkey` | `temporary-secret` | Shared passkey. In Azure, prefer a Key Vault reference. |
| `VideoAccess__SasLifetimeMinutes` | `60` | SAS lifetime, clamped to 1-120 minutes. |
| `VideoStorage__ConnectionString` | storage connection string | Must contain an account key so the app can sign the SAS. |
| `VideoStorage__ContainerName` | `klpt-videos` | Private Blob container containing the videos. |
| `VideoAssets__sample-video` | `sample-video.mp4` | Maps a public video ID to a blob name. Add one setting per video. |

Keep the container private. Add the KLPT site origin to:

- Function App CORS, so Angular can call `POST /api/videos/access`.
- Blob Storage CORS, allowing `GET` and `HEAD`, so the browser can stream and
  seek within the returned video URL.

Ensure MP4 blobs have `Content-Type: video/mp4`.

The endpoint uses anonymous Function authorization because the passkey is the
POC credential. Do not put a Function host key in the Angular application; it
would be visible to every browser user.

For local development, set `AzureWebJobsStorage` to `UseDevelopmentStorage=true`
when using Azurite, or to a valid development storage connection string.

## CI/CD

The GitHub Actions workflow in `.github/workflows/build-and-deploy.yml`:

- Restores and builds the Function app for pull requests targeting `master`.
- Publishes and deploys successful pushes to `master`.
- Can also be run manually from the GitHub Actions tab.

### One-time deployment setup

1. In the Azure portal, open the `klpt-functions` Function App.
2. Under **Configuration > General settings**, enable **SCM Basic Auth
   Publishing Credentials** if it is disabled.
3. Download the Function App publish profile.
4. In the GitHub repository, open **Settings > Secrets and variables >
   Actions**.
5. Create a repository secret named
   `AZURE_FUNCTIONAPP_PUBLISH_PROFILE`.
6. Paste the complete publish profile XML into the secret.

The workflow uses GitHub's `production` environment. Creating that environment
in **Settings > Environments** is optional, but allows deployment approvals and
environment-specific protection rules to be added later.

Application settings such as `VideoAccess__Passkey` and
`VideoStorage__ConnectionString` remain configured on the Azure Function App.
The deployment workflow does not overwrite them.
