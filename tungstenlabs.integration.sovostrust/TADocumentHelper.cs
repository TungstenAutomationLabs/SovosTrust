using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace tungstenlabs.integration.sovostrust
{
    /// <summary>
    /// TotalAgility SDK calls (CaptureDocumentService) used by the connector:
    /// read a document as PDF for Sovos, and store signed PDFs back in TA.
    /// Calls to the TA SDK bypass any system proxy (same as the ResistantAI connector).
    /// </summary>
    internal class TADocumentHelper
    {
        private readonly string _taSdkUrl;
        private readonly string _taSessionId;

        public TADocumentHelper(string taSdkUrl, string taSessionId)
        {
            if (string.IsNullOrWhiteSpace(taSdkUrl)) throw new ArgumentException("TASDKURL is required.");
            if (string.IsNullOrWhiteSpace(taSessionId)) throw new ArgumentException("TASession is required.");

            _taSdkUrl = taSdkUrl.TrimEnd('/');
            _taSessionId = taSessionId;
        }

        private static HttpClient CreateTAClient()
        {
            HttpClientHandler handler = new HttpClientHandler { Proxy = null, UseProxy = false };
            return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        }

        // -----------------------------------------------------------------
        // Read
        // -----------------------------------------------------------------

        /// <summary>
        /// Returns the TA document as PDF bytes. Prefers the original source file
        /// (GetSourceFile) when it is a PDF, otherwise asks TA for a PDF rendition
        /// (GetDocumentFile2 with FileType ".pdf"). Throws if neither yields a PDF,
        /// because FAD createRequisitionB2C takes a PDF.
        /// </summary>
        public byte[] GetDocumentAsPdf(string docId)
        {
            if (string.IsNullOrWhiteSpace(docId))
                throw new ArgumentException("DocID is required.");

            byte[] source = TryGetSourceFile(docId);
            if (IsPdf(source))
                return source;

            byte[] rendition = GetDocumentFile(docId, ".pdf");
            if (IsPdf(rendition))
                return rendition;

            throw new Exception("TA document " + docId + " could not be retrieved as a PDF. FAD needs a PDF; " +
                                "make sure the document is a PDF or that PDF generation has run in the process before this activity.");
        }

        private byte[] TryGetSourceFile(string docId)
        {
            try
            {
                var payload = new
                {
                    sessionId = _taSessionId,
                    reportingData = new { Station = "", MarkCompleted = false },
                    documentId = docId
                };

                using (HttpClient client = CreateTAClient())
                {
                    HttpResponseMessage response = client.PostAsync(_taSdkUrl + "/CaptureDocumentService.svc/json/GetSourceFile",
                        new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"))
                        .GetAwaiter().GetResult();

                    // "A source file for this document does not exist" is expected for image/split documents.
                    if (!response.IsSuccessStatusCode)
                        return null;

                    JObject json = JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    JToken body = json["d"] ?? json;
                    JToken sourceFile = body["SourceFile"];

                    if (sourceFile == null || sourceFile.Type == JTokenType.Null)
                        return null;

                    // TA serializes byte[] as a JSON array of numbers; accept base64 too for safety.
                    if (sourceFile.Type == JTokenType.Array)
                        return sourceFile.ToObject<byte[]>();

                    if (sourceFile.Type == JTokenType.String)
                    {
                        try { return Convert.FromBase64String(sourceFile.ToString()); }
                        catch (FormatException) { return null; }
                    }

                    return null;
                }
            }
            catch
            {
                // Fall back to GetDocumentFile2, same as the ResistantAI connector.
                return null;
            }
        }

        private byte[] GetDocumentFile(string docId, string fileType)
        {
            var payload = new
            {
                sessionId = _taSessionId,
                reportingData = new { Station = "", MarkCompleted = false },
                documentId = docId,
                documentFileOptions = new { FileType = fileType, IncludeAnnotations = 0 }
            };

            using (HttpClient client = CreateTAClient())
            {
                HttpResponseMessage response = client.PostAsync(_taSdkUrl + "/CaptureDocumentService.svc/json/GetDocumentFile2",
                    new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"))
                    .GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    string error = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    throw new Exception(string.Format("TA GetDocumentFile2 failed for document {0}: HTTP {1}. {2}",
                        docId, (int)response.StatusCode, SovosAuthHelper.Truncate(error, 500)));
                }

                return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
        }

        // -----------------------------------------------------------------
        // Write
        // -----------------------------------------------------------------

        /// <summary>
        /// Creates a new TA document from PDF bytes (CreateDocument3) and returns its DocumentId.
        /// Not used yet; kept for bringing signed PDFs back from FAD.
        /// parentFolderId may be empty to create a standalone document.
        /// </summary>
        public string CreateDocumentFromPdf(byte[] pdfBytes, string parentFolderId)
        {
            if (pdfBytes == null || pdfBytes.Length == 0)
                throw new ArgumentException("No PDF content to store in TotalAgility.");

            var payload = new
            {
                sessionId = _taSessionId,
                reportingData = (object)null,
                parentId = parentFolderId ?? "",
                runtimeFolderFields = (object)null,
                folderTypeId = "",
                documentDataInput = new
                {
                    // WCF JSON expects byte[] as an array of numbers, not base64.
                    Data = pdfBytes.Select(b => (int)b).ToArray(),
                    MimeType = "application/pdf",
                    PageDataList = (object)null,
                    RuntimeFields = (object)null
                },
                insertIndex = 0
            };

            using (HttpClient client = CreateTAClient())
            {
                HttpResponseMessage response = client.PostAsync(_taSdkUrl + "/CaptureDocumentService.svc/json/CreateDocument3",
                    new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"))
                    .GetAwaiter().GetResult();

                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                    throw new Exception(string.Format("TA CreateDocument3 failed: HTTP {0}. {1}",
                        (int)response.StatusCode, SovosAuthHelper.Truncate(body, 500)));

                JObject json = JObject.Parse(body);
                JToken documentId = json["d"] != null ? json["d"]["DocumentId"] : null;

                if (documentId == null || string.IsNullOrWhiteSpace(documentId.ToString()))
                    throw new Exception("TA CreateDocument3 did not return a DocumentId. Response: " + SovosAuthHelper.Truncate(body, 500));

                return documentId.ToString();
            }
        }

        // -----------------------------------------------------------------
        public static bool IsPdf(byte[] bytes)
        {
            // "%PDF"
            return bytes != null && bytes.Length > 4 &&
                   bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46;
        }
    }
}
