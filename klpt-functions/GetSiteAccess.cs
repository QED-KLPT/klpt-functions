using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace klpt_functions;

public sealed class GetSiteAccess
{
  private const int DefaultAccessTokenLifetimeMinutes = 60;
  private const int MaximumAccessTokenLifetimeMinutes = 240;
  private const string AccessTokenVersion = "v1";

  private readonly IConfiguration Configuration;
  private readonly ILogger<GetSiteAccess> Logger;

  public GetSiteAccess(
    IConfiguration configuration,
    ILogger<GetSiteAccess> logger)
  {
    Configuration = configuration;
    Logger = logger;
  }

  [Function("GetSiteAccess")]
  public async Task<IActionResult> Run(
    [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "site/access")]
    HttpRequest request,
    CancellationToken cancellationToken)
  {
    SiteAccessRequest? accessRequest;

    try
    {
      accessRequest = await JsonSerializer.DeserializeAsync<SiteAccessRequest>(
        request.Body,
        JsonOptions,
        cancellationToken);
    }
    catch (JsonException)
    {
      return new BadRequestObjectResult(
        new ErrorResponse("The request body must be valid JSON."));
    }

    var authenticationResult = Authenticate(accessRequest);
    if (!authenticationResult.IsAuthenticated)
    {
      if (authenticationResult.ConfigurationError)
      {
        Logger.LogError("Site access authentication settings are incomplete.");
        return new ObjectResult(new ErrorResponse("Site access is not configured."))
        {
          StatusCode = StatusCodes.Status500InternalServerError,
        };
      }

      Logger.LogWarning("Site access was denied.");

      return new ObjectResult(
        new ErrorResponse("The passkey or access token is invalid."))
      {
        StatusCode = StatusCodes.Status401Unauthorized,
      };
    }

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
      "Issued site access token, expiring at {ExpiresAt}.",
      accessTokenExpiresAt);

    return new OkObjectResult(
      new SiteAccessResponse(accessToken, accessTokenExpiresAt));
  }

  private AuthenticationResult Authenticate(SiteAccessRequest? request)
  {
    var configuredPasskey = Configuration["SiteAccess:Passkey"];
    var signingKey = Configuration["SiteAccess:SessionSigningKey"];

    if (string.IsNullOrWhiteSpace(configuredPasskey) ||
        string.IsNullOrWhiteSpace(signingKey))
    {
      return AuthenticationResult.ConfigurationFailure();
    }

    if (!string.IsNullOrWhiteSpace(request?.AccessToken) &&
        TryValidateAccessToken(request.AccessToken, signingKey, out var tokenExpiresAt))
    {
      return AuthenticationResult.Success(request.AccessToken, tokenExpiresAt);
    }

    if (!string.IsNullOrWhiteSpace(request?.Passkey) &&
        PasskeysMatch(request.Passkey, configuredPasskey))
    {
      return AuthenticationResult.Success();
    }

    return AuthenticationResult.Failure();
  }

  private string CreateAccessToken(out DateTimeOffset expiresAt)
  {
    var signingKey = Configuration["SiteAccess:SessionSigningKey"]!;
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

  private int GetAccessTokenLifetimeMinutes()
  {
    return int.TryParse(
      Configuration["SiteAccess:AccessTokenLifetimeMinutes"],
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

  public sealed record SiteAccessRequest(
    string? Passkey = null,
    string? AccessToken = null);

  public sealed record SiteAccessResponse(
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
