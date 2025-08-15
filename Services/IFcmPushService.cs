using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;

namespace Minis.Services
{
    public interface IFcmPushService
    {
        Task<string> SendAsync(
            string token,
            string title,
            string body,
            IDictionary<string, string>? data,
            CancellationToken ct);
    }

    public sealed class FcmPushService : IFcmPushService
    {
        private static readonly object _lock = new();
        private static bool _initialized;

        public FcmPushService(IConfiguration config)
        {
            EnsureFirebaseInitialized(config);
        }

        public async Task<string> SendAsync(
            string token,
            string title,
            string body,
            IDictionary<string, string>? data,
            CancellationToken ct)
        {
            var msg = new Message
            {
                Token = token,
                Notification = new Notification
                {
                    Title = title,
                    Body = body
                },
                // Firebase requires a mutable Dictionary<string,string>
                Data = data is null ? new Dictionary<string, string>() : new Dictionary<string, string>(data)
            };

            return await FirebaseMessaging.DefaultInstance.SendAsync(msg, ct);
        }

        private static void EnsureFirebaseInitialized(IConfiguration config)
        {
            if (_initialized && FirebaseApp.DefaultInstance is not null) return;

            lock (_lock)
            {
                if (_initialized && FirebaseApp.DefaultInstance is not null) return;

                var path = config["Fcm:ServiceAccountPath"] ?? config["Fcm:serviceAccountPath"];

                if (!File.Exists(path))
                    throw new FileNotFoundException($"FCM service account file not found at '{path}'.", path);

                FirebaseApp.Create(new AppOptions
                {
                    Credential = GoogleCredential.FromFile(path)
                });

                _initialized = true;
            }
        }
    }
}