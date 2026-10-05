using System;
using Newtonsoft.Json;

namespace tungstenlabs.integration.sovostrust
{
    // =====================================================================
    // FAD token response (POST /authorization-server/oauth/token)
    // =====================================================================
    internal class DO_FadToken
    {
        [JsonProperty("access_token")] public string access_token { get; set; }
        [JsonProperty("refresh_token")] public string refresh_token { get; set; }
        [JsonProperty("token_type")] public string token_type { get; set; }
        [JsonProperty("expires_in")] public int? expires_in { get; set; }
        [JsonProperty("scope")] public string scope { get; set; }
    }

    /// <summary>
    /// What is stored in the SOVOS-CLIENT-TOKEN server variable so tokens are reused
    /// across jobs instead of logging in for every document.
    /// </summary>
    internal class DO_CachedToken
    {
        [JsonProperty("access_token")] public string AccessToken { get; set; }
        [JsonProperty("refresh_token")] public string RefreshToken { get; set; }
        [JsonProperty("expires_at_utc")] public DateTime ExpiresAtUtc { get; set; }
    }
}
