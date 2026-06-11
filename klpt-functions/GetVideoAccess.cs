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

  private readonly IConfiguration _configuration;
  private readonly ILogger<GetVideoAccess> _logger;

  public GetVideoAccess(
      IConfiguration configuration,
      ILogger<GetVideoAccess> logger)
  {
    _configuration = configuration;
    _logger = logger;
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
      return new BadRequestObjectResult(new ErrorResponse("The request body must be valid JSON."));
    }

    if (string.IsNullOrWhiteSpace(accessRequest?.VideoId) ||
        string.IsNullOrWhiteSpace(accessRequest.Passkey))
    {
      return new BadRequestObjectResult(
          new ErrorResponse("Both videoId and passkey are required."));
    }

    var configuredPasskey = _configuration["VideoAccess:Passkey"];
    if (string.IsNullOrWhiteSpace(configuredPasskey))
    {
      _logger.LogError("VideoAccess:Passkey is not configured.");
      return new ObjectResult(new ErrorResponse("Video access is not configured."))
      {
        StatusCode = StatusCodes.Status500InternalServerError,
      };
    }

    if (!PasskeysMatch(accessRequest.Passkey, configuredPasskey))
    {
      _logger.LogWarning(
          "Video access was denied for video ID {VideoId}.",
          accessRequest.VideoId);

      return new ObjectResult(new ErrorResponse("The passkey is invalid."))
      {
        StatusCode = StatusCodes.Status401Unauthorized,
      };
    }

    var blobName = _configuration[$"VideoAssets:{accessRequest.VideoId}"];
    if (string.IsNullOrWhiteSpace(blobName))
    {
      return new NotFoundObjectResult(new ErrorResponse("The requested video was not found."));
    }

    var connectionString = _configuration["VideoStorage:ConnectionString"];
    var containerName = _configuration["VideoStorage:ContainerName"];
    if (string.IsNullOrWhiteSpace(connectionString) ||
        string.IsNullOrWhiteSpace(containerName))
    {
      _logger.LogError("Video storage settings are incomplete.");
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
        _logger.LogWarning(
            "Configured blob {BlobName} was not found in container {ContainerName}.",
            blobName,
            containerName);

        return new NotFoundObjectResult(
            new ErrorResponse("The requested video was not found."));
      }

      if (!blobClient.CanGenerateSasUri)
      {
        _logger.LogError(
            "The configured storage credential cannot generate a SAS URI. " +
            "Use a storage connection string containing an account key.");

        return new ObjectResult(new ErrorResponse("Video access is not configured."))
        {
          StatusCode = StatusCodes.Status500InternalServerError,
        };
      }

      var expiresAt = DateTimeOffset.UtcNow.AddMinutes(GetSasLifetimeMinutes());
      var sasBuilder = new BlobSasBuilder
      {
        BlobContainerName = containerName,
        BlobName = blobName,
        Resource = "b",
        ExpiresOn = expiresAt,
        Protocol = SasProtocol.Https,
        ContentDisposition = "inline",
      };
      sasBuilder.SetPermissions(BlobSasPermissions.Read);

      var sasUri = blobClient.GenerateSasUri(sasBuilder);

      _logger.LogInformation(
          "Issued video access for video ID {VideoId}, expiring at {ExpiresAt}.",
          accessRequest.VideoId,
          expiresAt);

      return new OkObjectResult(new VideoAccessResponse(sasUri.ToString(), expiresAt));
    }
    catch (Exception exception)
    {
      _logger.LogError(
          exception,
          "Could not issue video access for video ID {VideoId}.",
          accessRequest.VideoId);

      return new ObjectResult(new ErrorResponse("Video access could not be issued."))
      {
        StatusCode = StatusCodes.Status500InternalServerError,
      };
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

  private static bool PasskeysMatch(string suppliedPasskey, string configuredPasskey)
  {
    var suppliedBytes = Encoding.UTF8.GetBytes(suppliedPasskey);
    var configuredBytes = Encoding.UTF8.GetBytes(configuredPasskey);

    return suppliedBytes.Length == configuredBytes.Length &&
           CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
  }

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
  };

  public sealed record VideoAccessRequest(string VideoId, string Passkey);

  public sealed record VideoAccessResponse(string Url, DateTimeOffset ExpiresAt);

  public sealed record ErrorResponse(string Error);
}
