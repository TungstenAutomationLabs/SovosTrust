using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace tungstenlabs.integration.sovostrust
{
    /// <summary>Everything needed to log in to FAD (matches the Postman "Login" request).</summary>
    internal class FadCredentials
    {
        /// <summary>Token URL, e.g. https://api.firmaautografa.com/authorization-server/oauth/token</summary>
        public string AuthenticationUrl { get; set; }

        /// <summary>OAuth client (Postman Basic Auth username, e.g. "fad").</summary>
        public string ClientId { get; set; }

        /// <summary>OAuth client secret (Postman Basic Auth password).</summary>
        public string ClientSecret { get; set; }

        /// <summary>FAD user (form field "username").</summary>
        public string Username { get; set; }

        /// <summary>FAD password exactly as FAD expects it in the form field "password" (in the Postman collection it is already a SHA-256 hex string).</summary>
        public string Password { get; set; }
    }

    /// <summary>
    /// OAuth2 handling for the FAD API: password grant to log in, refresh_token grant to renew,
    /// tokens cached in a TotalAgility server variable (same idea as RAI-CLIENT-TOKEN in the
    /// ResistantAI connector).
    /// </summary>
    internal class SovosAuthHelper
    {
        public const string SOVOS_CLIENT_TOKEN = "SOVOS-CLIENT-TOKEN";
        public const string DEFAULT_TOKEN_URL = "https://api.firmaautografa.com/authorization-server/oauth/token";

        // Renew a little before the token actually expires.
        private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromMinutes(2);

        // -----------------------------------------------------------------
        // HttpClient
        // -----------------------------------------------------------------

        /// <summary>HttpClient for calls to FAD. Uses the server's default (system) network settings.</summary>
        public static HttpClient CreateSovosClient(int timeoutSeconds)
        {
            return new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        }

        public static void EnsureTls12()
        {
            // .NET 4.8 on older Windows Server builds may not default to TLS 1.2.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        // -----------------------------------------------------------------
        // Token handling
        // -----------------------------------------------------------------

        /// <summary>
        /// Returns a usable access token: the cached one if still valid, else a refreshed one
        /// (refresh_token grant), else a new login (password grant). Writes the result back to TA.
        /// </summary>
        public string GetValidToken(FadCredentials credentials, string taSessionId, string taSdkUrl)
        {
            DO_CachedToken cached = ReadCachedToken(taSessionId, taSdkUrl);

            if (cached != null && !string.IsNullOrWhiteSpace(cached.AccessToken) &&
                cached.ExpiresAtUtc - ExpirySafetyMargin > DateTime.UtcNow)
                return cached.AccessToken;

            return Renew(credentials, cached, taSessionId, taSdkUrl);
        }

        /// <summary>Ignores the cached access token (e.g. after a 401) and renews it.</summary>
        public string ForceRenew(FadCredentials credentials, string taSessionId, string taSdkUrl)
        {
            return Renew(credentials, ReadCachedToken(taSessionId, taSdkUrl), taSessionId, taSdkUrl);
        }

        private string Renew(FadCredentials credentials, DO_CachedToken cached, string taSessionId, string taSdkUrl)
        {
            DO_FadToken token = null;

            if (cached != null && !string.IsNullOrWhiteSpace(cached.RefreshToken))
            {
                try
                {
                    token = RequestToken(credentials, new[]
                    {
                        new KeyValuePair<string, string>("grant_type", "refresh_token"),
                        new KeyValuePair<string, string>("refresh_token", cached.RefreshToken)
                    });
                }
                catch
                {
                    token = null; // refresh token expired or revoked -> log in again
                }
            }

            if (token == null)
                token = Login(credentials);

            DO_CachedToken toCache = new DO_CachedToken
            {
                AccessToken = token.access_token,
                // Keep the old refresh token if FAD does not return a new one on refresh.
                RefreshToken = !string.IsNullOrWhiteSpace(token.refresh_token) ? token.refresh_token
                             : (cached != null ? cached.RefreshToken : null),
                ExpiresAtUtc = GetExpiry(token)
            };

            WriteCachedToken(toCache, taSessionId, taSdkUrl);
            return token.access_token;
        }

        /// <summary>Postman "Login": password grant. Public so the diagnostic helper can test credentials.</summary>
        public DO_FadToken Login(FadCredentials credentials)
        {
            if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password))
                throw new ArgumentException("FAD Username and Password are required.");

            return RequestToken(credentials, new[]
            {
                new KeyValuePair<string, string>("grant_type", "password"),
                new KeyValuePair<string, string>("username", credentials.Username),
                new KeyValuePair<string, string>("password", credentials.Password)
            });
        }

        private DO_FadToken RequestToken(FadCredentials credentials, KeyValuePair<string, string>[] form)
        {
            if (string.IsNullOrWhiteSpace(credentials.ClientId) || string.IsNullOrWhiteSpace(credentials.ClientSecret))
                throw new ArgumentException("FAD ClientID and ClientSecret (the Basic Auth pair on the Login request) are required.");

            string url = string.IsNullOrWhiteSpace(credentials.AuthenticationUrl) ? DEFAULT_TOKEN_URL : credentials.AuthenticationUrl.Trim();
            string grantType = form[0].Value;

            EnsureTls12();

            using (HttpClient client = CreateSovosClient(60))
            {
                string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.ClientId + ":" + credentials.ClientSecret));

                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new FormUrlEncodedContent(form);

                HttpResponseMessage response;
                try
                {
                    response = client.SendAsync(request).GetAwaiter().GetResult();
                }
                catch (HttpRequestException ex)
                {
                    throw new Exception("Could not reach the FAD token endpoint (" + url + "): " + ex.Message, ex);
                }

                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                    throw new Exception(string.Format("FAD token request ({0}) failed: HTTP {1} {2}. Response: {3}",
                        grantType, (int)response.StatusCode, response.ReasonPhrase, Truncate(body, 1000)));

                DO_FadToken token = JsonConvert.DeserializeObject<DO_FadToken>(body);
                if (token == null || string.IsNullOrWhiteSpace(token.access_token))
                    throw new Exception("FAD token response did not contain an access_token.");

                return token;
            }
        }

        /// <summary>Uses expires_in if present, else the JWT "exp" claim, else 30 minutes.</summary>
        private static DateTime GetExpiry(DO_FadToken token)
        {
            if (token.expires_in.HasValue && token.expires_in.Value > 0)
                return DateTime.UtcNow.AddSeconds(token.expires_in.Value);

            try
            {
                string[] parts = token.access_token.Split('.');
                if (parts.Length == 3)
                {
                    string payload = parts[1].Replace('-', '+').Replace('_', '/');
                    payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                    JObject claims = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                    JToken exp = claims["exp"];
                    if (exp != null)
                        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((long)exp);
                }
            }
            catch
            {
                // not a JWT we can read - fall through
            }

            return DateTime.UtcNow.AddMinutes(30);
        }

        private DO_CachedToken ReadCachedToken(string taSessionId, string taSdkUrl)
        {
            ServerVariableHelper helper = new ServerVariableHelper();
            var sv = helper.GetServerVariables(taSessionId, taSdkUrl, new List<string> { SOVOS_CLIENT_TOKEN });
            string raw = sv[SOVOS_CLIENT_TOKEN].Value;

            if (string.IsNullOrWhiteSpace(raw))
                return null;

            try
            {
                return JsonConvert.DeserializeObject<DO_CachedToken>(raw);
            }
            catch (JsonException)
            {
                // Not our JSON (e.g. a manually pasted value) - ignore and log in again.
                return null;
            }
        }

        private void WriteCachedToken(DO_CachedToken token, string taSessionId, string taSdkUrl)
        {
            ServerVariableHelper helper = new ServerVariableHelper();
            var sv = helper.GetServerVariables(taSessionId, taSdkUrl, new List<string> { SOVOS_CLIENT_TOKEN });
            string variableId = sv[SOVOS_CLIENT_TOKEN].Key;

            helper.UpdateServerVariables(
                new Dictionary<string, string> { { variableId, JsonConvert.SerializeObject(token) } },
                taSessionId, taSdkUrl);
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------
        public static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value.Substring(0, max) + "...";
        }
    }
}
