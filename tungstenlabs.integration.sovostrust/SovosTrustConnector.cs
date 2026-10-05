using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/*
 * tungstenlabs.integration.sovostrust.SovosTrustConnector
 *
 * TotalAgility .NET activity connector for Sovos Trust FAD (Firma Autografa Digital) API.
 * Modelled on the TungstenAutomationLabs ResistantAIConnector:
 *   - plain public methods callable from a TA .NET activity
 *   - OAuth tokens cached in a TA server variable (SOVOS-CLIENT-TOKEN)
 *   - documents read from TA through the CaptureDocumentService SDK
 *
 * Flow (from the FAD Postman collection):
 *   1. Login   POST {token url}  Basic(client id/secret), form: grant_type=password, username, password
 *              -> access_token, refresh_token, token_type
 *   2. Create  POST {api url}/requisitions/createRequisitionB2C  Bearer access_token
 *              multipart/form-data: xml (file), pdf (file), hash (text)
 */

namespace tungstenlabs.integration.sovostrust
{
    public class SovosTrustConnector
    {
        public const string DEFAULT_API_URL = "https://api.firmaautografa.com";
        public const string CREATE_REQUISITION_B2C_PATH = "/requisitions/createRequisitionB2C";

        // =================================================================
        // ENTRY POINTS - CREATE REQUISITION B2C
        // =================================================================

        /// <summary>
        /// Sends a TA document (PDF) plus its contract XML to FAD createRequisitionB2C.
        /// </summary>
        /// <param name="AuthenticationURL">FAD token URL. Empty = https://api.firmaautografa.com/authorization-server/oauth/token</param>
        /// <param name="ApiURL">FAD API base URL. Empty = https://api.firmaautografa.com</param>
        /// <param name="ClientID">OAuth client id (Basic Auth username on the Postman Login request).</param>
        /// <param name="ClientSecret">OAuth client secret (Basic Auth password on the Postman Login request).</param>
        /// <param name="Username">FAD user (Login form field "username").</param>
        /// <param name="Password">FAD password exactly as in the Login form field "password".</param>
        /// <param name="DocID">TA document to send (must be or render as PDF).</param>
        /// <param name="DocumentName">File name for the pdf part; ".pdf" added if missing. Empty = "document.pdf".</param>
        /// <param name="ContractXml">Contents of the requisition XML (the Postman "xml" file part).</param>
        /// <param name="Hash">Value for the "hash" field. Empty = SHA-256 (lowercase hex) of the PDF sent.</param>
        /// <param name="TASDKURL">TA SDK URL, e.g. http://server/TotalAgility/Services/Sdk</param>
        /// <param name="TASession">TA session id.</param>
        /// <param name="HashSent">The hash actually sent (useful when it was computed).</param>
        /// <param name="ResponseJson">Raw FAD response.</param>
        /// <returns>Requisition id if one is found in the response, otherwise empty (see ResponseJson).</returns>
        public string CreateRequisitionB2C(string AuthenticationURL, string ApiURL, string ClientID, string ClientSecret,
                                           string Username, string Password,
                                           string DocID, string DocumentName, string ContractXml, string Hash,
                                           string TASDKURL, string TASession,
                                           out string HashSent, out string ResponseJson)
        {
            return CreateRequisitionCore(Credentials(AuthenticationURL, ClientID, ClientSecret, Username, Password),
                ApiURL, DocID, DocumentName, ContractXml, Hash, TASDKURL, TASession, out HashSent, out ResponseJson);
        }

        /// <summary>
        /// Returns the SHA-256 (lowercase hex) of the PDF this connector would send for a TA document.
        /// Use it to compare with the hash FAD expects, or when the hash must also go inside the XML.
        /// </summary>
        public string GetDocumentPdfHash(string DocID, string TASDKURL, string TASession)
        {
            return Sha256Hex(new TADocumentHelper(TASDKURL, TASession).GetDocumentAsPdf(DocID));
        }

        // =================================================================
        // CORE
        // =================================================================

        private string CreateRequisitionCore(FadCredentials credentials, string apiUrl,
                                             string docId, string documentName, string contractXml, string hash,
                                             string taSdkUrl, string taSession,
                                             out string hashSent, out string responseJson)
        {
            hashSent = "";
            responseJson = "";

            if (string.IsNullOrWhiteSpace(contractXml))
                throw new ArgumentException("ContractXml is required (the 'xml' part of createRequisitionB2C).");

            byte[] pdf = new TADocumentHelper(taSdkUrl, taSession).GetDocumentAsPdf(docId);
            byte[] xml = new UTF8Encoding(false).GetBytes(contractXml);
            hashSent = string.IsNullOrWhiteSpace(hash) ? Sha256Hex(pdf) : hash.Trim();

            string url = (string.IsNullOrWhiteSpace(apiUrl) ? DEFAULT_API_URL : apiUrl.Trim()).TrimEnd('/') + CREATE_REQUISITION_B2C_PATH;

            SovosAuthHelper auth = new SovosAuthHelper();
            SovosAuthHelper.EnsureTls12();
            string token = auth.GetValidToken(credentials, taSession, taSdkUrl);

            using (HttpClient client = SovosAuthHelper.CreateSovosClient(300))
            {
                // Two attempts at most: the second only after a 401/403 with a renewed token.
                // No retry on 5xx/timeouts, because FAD may already have created the requisition.
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    HttpResponseMessage response = Send(client, url, token, pdf, NormalizePdfName(documentName), xml, hashSent);
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (response.IsSuccessStatusCode)
                    {
                        responseJson = body;
                        return FindValue(body, "requisitionId", "idRequisition", "id", "data.requisitionId", "data.idRequisition", "data.id", "requisition.id") ?? "";
                    }

                    if (attempt == 1 && (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden))
                    {
                        token = auth.ForceRenew(credentials, taSession, taSdkUrl);
                        continue;
                    }

                    throw new Exception(string.Format("FAD createRequisitionB2C failed: HTTP {0} {1}. Response: {2}",
                        (int)response.StatusCode, response.ReasonPhrase, SovosAuthHelper.Truncate(body, 1500)));
                }
            }

            throw new Exception("FAD createRequisitionB2C failed: token was rejected after renewal.");
        }

        private static HttpResponseMessage Send(HttpClient client, string url, string token,
                                                byte[] pdf, string pdfName, byte[] xml, string hash)
        {
            MultipartFormDataContent form = new MultipartFormDataContent();

            ByteArrayContent xmlPart = new ByteArrayContent(xml);
            xmlPart.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            form.Add(xmlPart, "xml", "contract.xml");

            ByteArrayContent pdfPart = new ByteArrayContent(pdf);
            pdfPart.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(pdfPart, "pdf", pdfName);

            form.Add(new StringContent(hash, Encoding.UTF8), "hash");

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                return client.SendAsync(request).GetAwaiter().GetResult();
            }
            catch (HttpRequestException ex)
            {
                throw new Exception("Could not reach FAD (" + url + "): " + ex.Message, ex);
            }
            catch (System.Threading.Tasks.TaskCanceledException ex)
            {
                throw new Exception("FAD createRequisitionB2C timed out. Check in FAD whether the requisition was created before retrying.", ex);
            }
        }

        // =================================================================
        // HELPERS
        // =================================================================

        private static FadCredentials Credentials(string authenticationUrl, string clientId, string clientSecret, string username, string password)
        {
            return new FadCredentials
            {
                AuthenticationUrl = authenticationUrl,
                ClientId = clientId,
                ClientSecret = clientSecret,
                Username = username,
                Password = password
            };
        }

        internal static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(digest.Length * 2);
                foreach (byte b in digest) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string NormalizePdfName(string name)
        {
            string value = string.IsNullOrWhiteSpace(name) ? "document" : name.Trim();
            return value.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? value : value + ".pdf";
        }

        /// <summary>Returns the first non-empty scalar value among dotted paths in a JSON object.</summary>
        private static string FindValue(string json, params string[] paths)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            JToken root;
            try { root = JToken.Parse(json); }
            catch (JsonException) { return null; }

            if (root.Type != JTokenType.Object) return null;

            foreach (string path in paths)
            {
                JToken value = root.SelectToken(path);
                if (value != null && value.Type != JTokenType.Null && value.Type != JTokenType.Object && value.Type != JTokenType.Array)
                {
                    string s = value.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return null;
        }
    }
}
