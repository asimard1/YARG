using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine.Networking;
using YARG.Core.Logging;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Player;

namespace YARG.Online
{
    /// <summary>
    /// Per-session auth state for talking to the YARG server: caches a dev-auth
    /// bearer token plus the identity it minted, refreshes on demand, and feeds
    /// <see cref="LobbyHubSession"/>'s SignalR <c>AccessTokenProvider</c>.
    /// One instance is created when the player enters the online flow and held
    /// by the active <see cref="LobbyHubSession"/>.
    /// </summary>
    public sealed class OnlineAccessTokenProvider
    {
        // TODO: surface as a setting once the online flow stabilizes.
        //public const string BaseUrl = "http://localhost:5230";
        public const string BaseUrl = "https://rjoghqrt4k6k.shares.zrok.io";

        private const string DevAuthPath = "/api/v1/auth/dev";

        // Refresh a little before the server-declared expiry so an in-flight
        // request never lands with an expired token.
        private static readonly TimeSpan ExpiryGrace = TimeSpan.FromSeconds(30);

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly string _authName;
        private readonly object _lock = new();

        private string _accessToken;
        private DateTimeOffset _expiresAt;

        public string UserId { get; private set; }
        public string DisplayName { get; private set; }

        public bool HasValidToken
        {
            get
            {
                lock (_lock)
                {
                    return !string.IsNullOrEmpty(_accessToken)
                        && DateTimeOffset.UtcNow < _expiresAt - ExpiryGrace;
                }
            }
        }

        public OnlineAccessTokenProvider(string authName)
        {
            _authName = SanitizeAuthName(authName);
        }

        public static string ResolveDefaultAuthName()
        {
            var name = PlayerContainer.Players.FirstOrDefault()?.Profile.Name;
            return SanitizeAuthName(name);
        }

        // Server-side validation rejects names with anything outside
        // [A-Za-z0-9 _-] (e.g. "Mic'd Up" fails because of the apostrophe).
        // Replace each disallowed char with an underscore so the player's
        // profile name still resolves to a recognizable identity rather than
        // silently falling back to "YARG-Player".
        private static readonly Regex DisallowedAuthNameChars = new(@"[^A-Za-z0-9 _-]", RegexOptions.Compiled);

        private static string SanitizeAuthName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "YARG-Player";
            }
            var sanitized = DisallowedAuthNameChars.Replace(name, "_").Trim();
            return string.IsNullOrEmpty(sanitized) ? "YARG-Player" : sanitized;
        }

        /// <summary>
        /// Acquire a token if none is cached or the cached one has (nearly) expired.
        /// No-op when <see cref="HasValidToken"/> is true. Throws on failure.
        /// </summary>
        public async UniTask EnsureAuthenticatedAsync(CancellationToken ct = default)
        {
            if (HasValidToken)
            {
                YargLogger.LogInfo($"OnlineAccessTokenProvider: reusing cached token (expires {_expiresAt})");
                return;
            }
            await DevAuthAndCacheAsync(ct);
        }

        /// <summary>
        /// Returns a non-expired bearer token, re-authenticating if needed.
        /// Assigned directly to SignalR's <c>options.AccessTokenProvider</c>
        /// so reconnects automatically pick up a fresh token.
        /// </summary>
        public async Task<string> GetAccessTokenAsync()
        {
            if (!HasValidToken)
            {
                await DevAuthAndCacheAsync(CancellationToken.None);
            }
            lock (_lock) return _accessToken;
        }

        private async UniTask DevAuthAndCacheAsync(CancellationToken ct)
        {
            YargLogger.LogInfo($"OnlineAccessTokenProvider: requesting dev auth as '{_authName}' at {BaseUrl}");
            try
            {
                var response = await PerformDevAuthAsync(ct);
                lock (_lock)
                {
                    _accessToken = response.Token;
                    _expiresAt = response.ExpiresAt;
                    UserId = response.UserId;
                    DisplayName = response.DisplayName;
                }
                YargLogger.LogInfo($"OnlineAccessTokenProvider: dev auth ok — userId={UserId}, expires={_expiresAt}");
            }
            catch (Exception ex)
            {
                YargLogger.LogError($"OnlineAccessTokenProvider: dev auth failed — {ex.Message}");
                throw;
            }
        }

        private async UniTask<DevAuthResponse> PerformDevAuthAsync(CancellationToken ct)
        {
            var url = BaseUrl.TrimEnd('/') + DevAuthPath;
            var body = JsonConvert.SerializeObject(new DevAuthRequest(_authName), JsonSettings);

            using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body))
            {
                contentType = "application/json",
            };
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Accept", "application/json");

            await req.SendWebRequest().ToUniTask(cancellationToken: ct);

            if (req.result != UnityWebRequest.Result.Success)
            {
                throw new InvalidOperationException(
                    $"Dev auth failed ({(int) req.responseCode} {req.error}): {req.downloadHandler.text}");
            }

            var response = JsonConvert.DeserializeObject<DevAuthResponse>(req.downloadHandler.text, JsonSettings);
            if (response is null)
            {
                throw new InvalidOperationException("Dev auth response was empty.");
            }
            return response;
        }
    }
}
