using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace klpt_functions;

public sealed class GetVideoAccess
{
  private const int DefaultSasLifetimeMinutes = 60;
  private const int MaximumSasLifetimeMinutes = 120;
  private const int DefaultAccessTokenLifetimeMinutes = 60;
  private const int MaximumAccessTokenLifetimeMinutes = 240;
  private const string AccessTokenVersion = "v1";

  private readonly IConfiguration _configuration;
  private readonly ILogger<GetVideoAccess> Logger;

  public GetVideoAccess(
    IConfiguration configuration,
    ILogger<GetVideoAccess> logger)
  {
    _configuration = configuration;
    Logger = logger;
  }

  [Function("GetVideoAccess")]
  public async Task<IActionResult> Run(
    [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "videos/access")]
    HttpRequest request,
    CancellationToken cancellationToken)
  {
    VideoAccessRequest? accessRequest;

    try
    {
      accessRequest = await JsonSerializer.DeserializeAsync<VideoAccessRequest>(
        request.Body,
        JsonOptions,
        cancellationToken);
    }
    catch (JsonException)
    {
      return new BadRequestObjectResult(
        new ErrorResponse("The request body must be valid JSON."));
    }

    if (string.IsNullOrWhiteSpace(accessRequest?.VideoId))
    {
      return new BadRequestObjectResult(new ErrorResponse("videoId is required."));
    }

    var authenticationResult = Authenticate(accessRequest);
    if (!authenticationResult.IsAuthenticated)
    {
      if (authenticationResult.ConfigurationError)
      {
        Logger.LogError("Video access authentication settings are incomplete.");
        return new ObjectResult(new ErrorResponse("Video access is not configured."))
        {
          StatusCode = StatusCodes.Status500InternalServerError,
        };
      }

      Logger.LogWarning(
        "Video access was denied for video ID {VideoId}.",
        accessRequest.VideoId);

      return new ObjectResult(
        new ErrorResponse("The passkey or access token is invalid."))
      {
        StatusCode = StatusCodes.Status401Unauthorized,
      };
    }

    var blobName = _configuration[$"VideoAssets:{accessRequest.VideoId}"];
    if (string.IsNullOrWhiteSpace(blobName))
    {
      return new NotFoundObjectResult(
        new ErrorResponse("The requested video was not found."));
    }

    var connectionString = _configuration["VideoStorage:ConnectionString"];
    var containerName = _configuration["VideoStorage:ContainerName"];
    if (string.IsNullOrWhiteSpace(connectionString) ||
        string.IsNullOrWhiteSpace(containerName))
    {
      Logger.LogError("Video storage settings are incomplete.");
      return new ObjectResult(new ErrorResponse("Video storage is not configured."))
      {
        StatusCode = StatusCodes.Status500InternalServerError,
      };
    }

    try
    {
      var containerClient = new BlobContainerClient(connectionString, containerName);
      var blobClient = containerClient.GetBlobClient(blobName);

      if (!await blobClient.ExistsAsync(cancellationToken))
      {
        Logger.LogWarning(
          "Configured blob {BlobName} was not found in container {ContainerName}.",
          blobName,
          containerName);

        return new NotFoundObjectResult(
          new ErrorResponse("The requested video was not found."));
      }

      if (!blobClient.CanGenerateSasUri)
      {
        Logger.LogError(
          "The configured storage credential cannot generate a SAS URI. " +
          "Use a storage connection string containing an account key.");

        return new ObjectResult(new ErrorResponse("Video access is not configured."))
        {
          StatusCode = StatusCodes.Status500InternalServerError,
        };
      }

      var sasExpiresAt = DateTimeOffset.UtcNow.AddMinutes(GetSasLifetimeMinutes());
      var sasBuilder = new BlobSasBuilder
      {
        BlobContainerName = containerName,
        BlobName = blobName,
        Resource = "b",
        ExpiresOn = sasExpiresAt,
        Protocol = SasProtocol.Https,
        ContentDisposition = "inline",
      };
      sasBuilder.SetPermissions(BlobSasPermissions.Read);

      var sasUri = blobClient.GenerateSasUri(sasBuilder);
      string accessToken;
      DateTimeOffset accessTokenExpiresAt;
      if (authenticationResult.AccessToken is not null &&
          authenticationResult.AccessTokenExpiresAt.HasValue)
      {
        accessToken = authenticationResult.AccessToken;
        accessTokenExpiresAt = authenticationResult.AccessTokenExpiresAt.Value;
      }
      else
      {
        accessToken = CreateAccessToken(out accessTokenExpiresAt);
      }

      Logger.LogInformation(
        "Issued video access for video ID {VideoId}, expiring at {ExpiresAt}.",
        accessRequest.VideoId,
        sasExpiresAt);

      return new OkObjectResult(
        new VideoAccessResponse(
          accessRequest.VideoId,
          sasUri.ToString(),
          sasExpiresAt,
          accessToken,
          accessTokenExpiresAt));
    }
    catch (Exception exception)
    {
      Logger.LogError(
        exception,
        "Could not issue video access for video ID {VideoId}.",
        accessRequest.VideoId);

      return new ObjectResult(new ErrorResponse("Video access could not be issued."))
      {
        StatusCode = StatusCodes.Status500InternalServerError,
      };
    }
  }

  private AuthenticationResult Authenticate(VideoAccessRequest request)
  {
    var configuredPasskey = _configuration["VideoAccess:Passkey"];
    var signingKey = _configuration["VideoAccess:SessionSigningKey"];

    if (string.IsNullOrWhiteSpace(configuredPasskey) ||
        string.IsNullOrWhiteSpace(signingKey))
    {
      return AuthenticationResult.ConfigurationFailure();
    }

    if (!string.IsNullOrWhiteSpace(request.AccessToken) &&
        TryValidateAccessToken(request.AccessToken, signingKey, out var tokenExpiresAt))
    {
      return AuthenticationResult.Success(request.AccessToken, tokenExpiresAt);
    }

    if (!string.IsNullOrWhiteSpace(request.Passkey) &&
        PasskeysMatch(request.Passkey, configuredPasskey))
    {
      return AuthenticationResult.Success();
    }

    return AuthenticationResult.Failure();
  }

  private string CreateAccessToken(out DateTimeOffset expiresAt)
  {
    var signingKey = _configuration["VideoAccess:SessionSigningKey"]!;
    expiresAt = DateTimeOffset.UtcNow.AddMinutes(GetAccessTokenLifetimeMinutes());
    var nonce = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
    var payload = $"{AccessTokenVersion}.{expiresAt.ToUnixTimeSeconds()}.{nonce}";
    var signature = ComputeSignature(payload, signingKey);

    return $"{payload}.{Base64UrlEncode(signature)}";
  }

  private static bool TryValidateAccessToken(
    string accessToken,
    string signingKey,
    out DateTimeOffset expiresAt)
  {
    expiresAt = default;
    var parts = accessToken.Split('.');
    if (parts.Length != 4 ||
        parts[0] != AccessTokenVersion ||
        !long.TryParse(parts[1], out var expiresAtUnixSeconds))
    {
      return false;
    }

    try
    {
      var suppliedSignature = Base64UrlDecode(parts[3]);
      var expectedSignature = ComputeSignature(
        $"{parts[0]}.{parts[1]}.{parts[2]}",
        signingKey);
      expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds);

      return expiresAt > DateTimeOffset.UtcNow &&
             suppliedSignature.Length == expectedSignature.Length &&
             CryptographicOperations.FixedTimeEquals(
               suppliedSignature,
               expectedSignature);
    }
    catch (FormatException)
    {
      return false;
    }
    catch (ArgumentOutOfRangeException)
    {
      return false;
    }
  }

  private int GetSasLifetimeMinutes()
  {
    return int.TryParse(
      _configuration["VideoAccess:SasLifetimeMinutes"],
      out var configuredMinutes)
      ? Math.Clamp(configuredMinutes, 1, MaximumSasLifetimeMinutes)
      : DefaultSasLifetimeMinutes;
  }

  private int GetAccessTokenLifetimeMinutes()
  {
    return int.TryParse(
      _configuration["VideoAccess:AccessTokenLifetimeMinutes"],
      out var configuredMinutes)
      ? Math.Clamp(configuredMinutes, 1, MaximumAccessTokenLifetimeMinutes)
      : DefaultAccessTokenLifetimeMinutes;
  }

  private static bool PasskeysMatch(string suppliedPasskey, string configuredPasskey)
  {
    var suppliedBytes = Encoding.UTF8.GetBytes(suppliedPasskey);
    var configuredBytes = Encoding.UTF8.GetBytes(configuredPasskey);

    return suppliedBytes.Length == configuredBytes.Length &&
           CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
  }

  private static byte[] ComputeSignature(string payload, string signingKey)
  {
    return HMACSHA256.HashData(
      Encoding.UTF8.GetBytes(signingKey),
      Encoding.UTF8.GetBytes(payload));
  }

  private static string Base64UrlEncode(byte[] value)
  {
    return Convert.ToBase64String(value)
      .TrimEnd('=')
      .Replace('+', '-')
      .Replace('/', '_');
  }

  private static byte[] Base64UrlDecode(string value)
  {
    var base64 = value.Replace('-', '+').Replace('_', '/');
    base64 = base64.PadRight(
      base64.Length + ((4 - base64.Length % 4) % 4),
      '=');
    return Convert.FromBase64String(base64);
  }

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
  };

  public sealed record VideoAccessRequest(
    string VideoId,
    string? Passkey = null,
    string? AccessToken = null);

  public sealed record VideoAccessResponse(
    string VideoId,
    string Url,
    DateTimeOffset ExpiresAt,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt);

  public sealed record ErrorResponse(string Error);

  private sealed record AuthenticationResult(
    bool IsAuthenticated,
    bool ConfigurationError,
    string? AccessToken,
    DateTimeOffset? AccessTokenExpiresAt)
  {
    public static AuthenticationResult Success(
      string? accessToken = null,
      DateTimeOffset? accessTokenExpiresAt = null) =>
      new(true, false, accessToken, accessTokenExpiresAt);

    public static AuthenticationResult Failure() =>
      new(false, false, null, null);

    public static AuthenticationResult ConfigurationFailure() =>
      new(false, true, null, null);
  }
}
