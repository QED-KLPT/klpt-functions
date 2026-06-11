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
