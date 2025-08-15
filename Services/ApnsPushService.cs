using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace Minis.Services
{
    public interface IApnsPushService
    {
        Task<(bool ok, string status, string? body)> SendAsync(
            string deviceToken,
            string title,
            string body,
            IDictionary<string, string>? data = null,
            CancellationToken ct = default);
    }

    public sealed class ApnsPushService : IApnsPushService
    {
        private readonly ECDsa _key;
        private readonly string _keyId;
        private readonly string _teamId;
        private readonly string _bundleId;
        private readonly bool _useSandbox;
        private readonly SigningCredentials _creds;
        private static readonly HttpClient _http = new HttpClient();

        public ApnsPushService(IConfiguration cfg)
        {
            var section = cfg.GetSection("Apns");
            var p8Path = section["PrivateKeyPath"] ?? throw new InvalidOperationException("Apns:PrivateKeyPath missing");
            _keyId = section["KeyId"] ?? throw new InvalidOperationException("Apns:KeyId missing");
            _teamId = section["TeamId"] ?? throw new InvalidOperationException("Apns:TeamId missing");
            _bundleId = section["BundleId"] ?? throw new InvalidOperationException("Apns:BundleId missing");
            _useSandbox = bool.TryParse(section["UseSandbox"], out var b) ? b : true;

            var pem = File.ReadAllText(p8Path).Trim();
            _key = ECDsa.Create();
            _key.ImportFromPem(pem); // .NET 6+ can read .p8 PEM directly

            var ecdsaKey = new ECDsaSecurityKey(_key) { KeyId = _keyId };
            _creds = new SigningCredentials(ecdsaKey, SecurityAlgorithms.EcdsaSha256);
        }

        private string CreateJwt()
        {
            var handler = new JwtSecurityTokenHandler();
            var desc = new SecurityTokenDescriptor
            {
                Issuer = _teamId,
                IssuedAt = DateTime.UtcNow,
                SigningCredentials = _creds,
                AdditionalHeaderClaims = new Dictionary<string, object> { { "kid", _keyId } }
            };
            var token = handler.CreateJwtSecurityToken(desc);
            return handler.WriteToken(token);
        }

        public async Task<(bool ok, string status, string? body)> SendAsync(
            string deviceToken,
            string title,
            string body,
            IDictionary<string, string>? data = null,
            CancellationToken ct = default)
        {
            var jwt = CreateJwt();

            // Build APNs JSON (custom data lives at root, not inside aps)
            var root = new Dictionary<string, object>
            {
                ["aps"] = new Dictionary<string, object>
                {
                    ["alert"] = new { title, body },
                    ["sound"] = "default"
                }
            };
            if (data != null)
                foreach (var kv in data) root[kv.Key] = kv.Value;

            var json = JsonSerializer.Serialize(root);
            var host = _useSandbox ? "https://api.sandbox.push.apple.com" : "https://api.push.apple.com";
            var url = $"{host}/3/device/{deviceToken}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Version = new Version(2, 0),
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("bearer", jwt);
            req.Headers.TryAddWithoutValidation("apns-topic", _bundleId);
            req.Headers.TryAddWithoutValidation("apns-push-type", "alert");

            var resp = await _http.SendAsync(req, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            return (resp.IsSuccessStatusCode, resp.StatusCode.ToString(), respBody);
        }
    }
}