# Sovos Trust (FAD) Connector for Tungsten TotalAgility

A .NET Framework 4.8 class library (`tungstenlabs.integration.sovostrust.dll`) that lets a TotalAgility process send a document to Sovos Trust **FAD (Firma Autógrafa Digital)** through the `createRequisitionB2C` API.

It follows the same pattern as the [ResistantAIConnector](https://github.com/TungstenAutomationLabs/ResistantAIConnector): public methods called from a TA **.NET activity**, the OAuth token cached in a TA server variable, and document access through the TA SDK (`CaptureDocumentService`).

---

## What it does (mirrors the FAD Postman collection)

| Postman request | Connector |
|---|---|
| **Login** — `POST https://api.firmaautografa.com/authorization-server/oauth/token`, Basic Auth (client id / secret), form `grant_type=password`, `username`, `password` → `access_token`, `refresh_token` | `SovosAuthHelper`: logs in, caches both tokens in TA, renews with `grant_type=refresh_token` when the access token expires, and falls back to a fresh login if the refresh fails |
| **Create Requisition B2C** — `POST https://api.firmaautografa.com/requisitions/createRequisitionB2C`, Bearer token, multipart form `xml` (file), `pdf` (file), `hash` (text) | `CreateRequisitionB2C`: reads the TA document as PDF, sends it with your contract XML and the hash |

---

## Build

1. Open `tungstenlabs.integration.sovostrust.sln` in Visual Studio 2019/2022.
2. Restore NuGet packages (Newtonsoft.Json 13.0.3, ILMerge 3.0.41, MSBuild.ILMerge.Task 1.1.3 — same as the RAI project).
3. Build **Release**. ILMerge folds Newtonsoft.Json in and **internalizes** it (`<ILMergeInternalize>true</ILMergeInternalize>` in `ILMerge.props`), so you deploy a single `tungstenlabs.integration.sovostrust.dll` from `bin\Release` and TotalAgility only sees the connector's own public class, not the Newtonsoft types.
4. The assembly is strong-named with the included `tungstenlabs.integration.sovostrust.snk` (a new key). Replace it with your organisation's key if you have one.

## Deploy to TotalAgility

Use the same procedure you use for the ResistantAI DLL, then reference class `tungstenlabs.integration.sovostrust.SovosTrustConnector` in a .NET activity.

### Server variable (required)

| Name | Secure | Value |
|---|---|---|
| `SOVOS-CLIENT-TOKEN` | **No** (the DLL reads and writes it) | leave empty — the connector fills it |

It holds `{"access_token", "refresh_token", "expires_at_utc"}`. Expiry comes from `expires_in` or, if absent, from the JWT `exp` claim. On a 401/403 the connector renews once and retries.

### Network

Calls to FAD use the server's default network settings: they go direct, or through the Windows system proxy if one is configured for the account TotalAgility runs under. Calls to the TA SDK always go direct.

### Credentials

Keep these in TA (secure server variables mapped into process variables is a good fit), not in the process map itself:

| Input | Postman equivalent |
|---|---|
| `AuthenticationURL` | Login URL (empty = `https://api.firmaautografa.com/authorization-server/oauth/token`) |
| `ApiURL` | Base URL (empty = `https://api.firmaautografa.com`) |
| `ClientID` / `ClientSecret` | Login → Authorization → Basic Auth username / password |
| `Username` / `Password` | Login → Body → `username` / `password` (passed exactly as given; in the collection the password is already a SHA-256 hex string) |

---

## Methods

All methods throw on failure, so TA suspends the activity with a readable message.

### `CreateRequisitionB2C`

| Parameter | Notes |
|---|---|
| `AuthenticationURL`, `ApiURL`, `ClientID`, `ClientSecret`, `Username`, `Password` | See above. |
| `DocID` | TA document. Must be a PDF or have a PDF rendition (run PDF generation first for images). |
| `DocumentName` | File name for the `pdf` part; `.pdf` added if missing. |
| `ContractXml` | Text of the requisition XML (the Postman `xml` file, e.g. `sample_contract.xml`). Build it in TA from process data. |
| `Hash` | Value for the `hash` field. **Empty = SHA-256 of the PDF being sent** (lowercase hex). |
| `TASDKURL`, `TASession` | TA SDK URL and session. |
| **returns** | Requisition id if found in the response (`requisitionId`, `idRequisition`, `id`, or the same under `data`), else empty. |
| `out HashSent` | The hash actually sent. |
| `out ResponseJson` | Raw FAD response — store it on the job. |

The call is **not retried** on timeouts or 5xx, because FAD may already have created the requisition. A timeout raises an error that says to check FAD before retrying.

### `GetDocumentPdfHash`
Returns the SHA-256 of the PDF the connector would send for a `DocID`. Useful if the same hash also has to appear inside the XML, or to compare against what FAD expects.

---

## To confirm with Sovos / FAD

1. **`hash`** — the connector assumes SHA-256 of the PDF (the sample value is 64 hex characters). Check this against one of your Postman runs: compute the SHA-256 of `PruebaFADWEBVent.pdf` and compare it to the `hash` you sent. If it is the hash of something else (the XML, or PDF + XML), pass it in `Hash` or tell me and I'll change the default.
2. **XML content** — the connector sends whatever you pass. Share `sample_contract.xml` (with real data removed) if you want the connector to build it from TA fields.
3. **Response shape** — confirm which field holds the requisition id so the return value is reliable.
4. **Next steps after creation** — status checks and downloading the signed PDF need further FAD endpoints (not in this collection). `TADocumentHelper.CreateDocumentFromPdf` is already there to store a signed PDF back in TA.

---

## Project layout

```
tungstenlabs.integration.sovostrust.sln
tungstenlabs.integration.sovostrust/
  SovosTrustConnector.cs   public entry points (CreateRequisitionB2C, GetDocumentPdfHash)
  SovosAuthHelper.cs       FAD login / refresh, token cache in TA, TLS 1.2
  TADocumentHelper.cs      read TA document as PDF, create TA documents from PDFs
  ServerVariableHelper.cs  TA server variables (same as the RAI connector)
  Models.cs                token models
```
