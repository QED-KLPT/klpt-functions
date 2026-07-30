# KLPT Functions

## Protected video access

`POST /api/videos/access` accepts an allow-listed video ID and either the shared
passkey or a previously issued access token. It returns a short-lived, read-only
SAS URL for that video plus a reusable application access token.

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
  "videoId": "sample-video",
  "url": "https://example.blob.core.windows.net/klpt-videos/sample-video.mp4?...",
  "expiresAt": "2026-06-11T03:00:00+00:00",
  "accessToken": "v1.1781146800.nonce.signature",
  "accessTokenExpiresAt": "2026-06-11T03:00:00+00:00"
}
```

For another video during the access-token lifetime, omit the passkey:

```json
{
  "videoId": "another-video",
  "accessToken": "v1.1781146800.nonce.signature"
}
```

Configure these application settings locally and in the Azure Function App:

| Setting | Example | Purpose |
| --- | --- | --- |
| `VideoAccess__Passkey` | `temporary-secret` | Shared passkey. In Azure, prefer a Key Vault reference. |
| `VideoAccess__SessionSigningKey` | random 32+ byte secret | Signs the reusable access token. Store it as a secret or Key Vault reference. |
| `VideoAccess__AccessTokenLifetimeMinutes` | `60` | Time during which another video can be requested without the passkey, clamped to 1-240 minutes. |
| `VideoAccess__SasLifetimeMinutes` | `60` | SAS lifetime, clamped to 1-120 minutes. |
| `VideoStorage__ConnectionString` | storage connection string | Must contain an account key so the app can sign the SAS. |
| `VideoStorage__ContainerName` | `klpt-videos` | Private Blob container containing the videos. |

Video IDs are allow-listed in `klpt-functions/vid-mapping.json`. Add one entry
per protected video, mapping the public ID used by the client to its blob name:

```json
{
  "sample-vid-smash-cake": "sample-vid-smash-cake.mp4"
}
```

Keep the container private. Add the KLPT site origin to:

- Function App CORS, so Angular can call `POST /api/videos/access`.
- Blob Storage CORS, allowing `GET` and `HEAD`, so the browser can stream and
  seek within the returned video URL.

Ensure MP4 blobs have `Content-Type: video/mp4`.

The endpoint uses anonymous Function authorization because the passkey is the
POC credential. Do not put a Function host key in the Angular application; it
would be visible to every browser user.

Each SAS remains scoped to one blob. The reusable access token authorizes the
client to request another video-specific SAS without submitting the passkey
again. The client should discard both values after their respective expiry
times.

For local development, set `AzureWebJobsStorage` to `UseDevelopmentStorage=true`
when using Azurite, or to a valid development storage connection string.

## Protected site access

`POST /api/site/access` accepts a shared passkey and returns a reusable access
token. Use this endpoint to gate entry to the site before the user can access
any protected content.

Example request (first visit — supply the passkey):

```json
{
  "passkey": "the-shared-passkey"
}
```

Example response:

```json
{
  "accessToken": "v1.1781146800.nonce.signature",
  "accessTokenExpiresAt": "2026-06-25T11:00:00+00:00"
}
```

On subsequent requests during the token lifetime, omit the passkey and supply
the token instead:

```json
{
  "accessToken": "v1.1781146800.nonce.signature"
}
```

Configure these application settings locally and in the Azure Function App:

| Setting | Example | Purpose |
| --- | --- | --- |
| `SiteAccess__Passkey` | `temporary-secret` | Shared passkey. In Azure, prefer a Key Vault reference. |
| `SiteAccess__SessionSigningKey` | random 32+ byte secret | Signs the reusable access token. Store it as a secret or Key Vault reference. |
| `SiteAccess__AccessTokenLifetimeMinutes` | `60` | Token lifetime, clamped to 1-240 minutes. Defaults to 60. |

The endpoint uses anonymous Function authorization because the passkey is the
credential. Do not put a Function host key in the Angular application; it would
be visible to every browser user.

The client should store the access token and reuse it for the token's lifetime,
then prompt the user for the passkey again when it expires.

## CI/CD

The GitHub Actions workflow in `.github/workflows/build-and-deploy.yml`:

- Restores and builds the Function app for pull requests targeting `main` or
  `dev`.
- Publishes and deploys successful pushes to `main` to `klpt-functions`.
- Publishes and deploys successful pushes to `dev` to `klpt-functions-dev`.
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
7. Repeat the process for the `klpt-functions-dev` Function App and save its
   complete publish profile as the repository secret
   `AZURE_FUNCTIONAPP_PUBLISH_PROFILE_DEV`.

The workflow uses GitHub's `production` and `development` environments. Creating
these environments in **Settings > Environments** is optional, but allows
deployment approvals and environment-specific protection rules to be added
later.

Application settings such as `VideoAccess__Passkey` and
`VideoStorage__ConnectionString` remain configured on the Azure Function App.
The deployment workflow does not overwrite them.

Azure Monitor export is enabled automatically when the Function App has an
`APPLICATIONINSIGHTS_CONNECTION_STRING` application setting. The worker still
starts when Application Insights has not yet been connected.

The Function App uses the Flex Consumption plan, so the deployment action is
configured with `sku: flexconsumption`. This selects OneDeploy rather than the
classic Kudu ZipDeploy endpoint. The build artifact must include the generated
hidden `.azurefunctions` directory at its root; the workflow explicitly
preserves hidden files when transferring the package between jobs.
