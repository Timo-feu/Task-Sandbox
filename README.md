# TaskSandbox — model and capability discovery step

Target framework: `net10.0`.
No external NuGet packages are required. `backend.csproj` remains unchanged.

## Changes

- Adds optional `GET /v1/models` discovery to `IModelDiscovery` / `OpenAiCompatibleProvider`.
- Treats HTTP 404, 405, and 501 as an unsupported model-list endpoint; other HTTP failures remain visible.
- Parses standard model `id` and optional `owned_by` fields.
- Supports an optional, non-standard per-model `capabilities` object containing boolean fields: `chat_completions`, `streaming`, `json_output`, `tool_calling`, and `vision`. Missing fields remain `Unknown`; no capabilities are guessed.
- Reports capabilities implemented by this adapter separately: Chat Completions and Streaming are supported; JSON Output, Tool Calling, and Vision are not implemented by this adapter yet.
- Selects the configured preferred model (`local-model`) when it is listed; otherwise uses the first discovered model. If discovery is unsupported or returns no models, the configured model remains in use.
- Adds an optional `ModelId` override to `ModelRequest`; omitted values continue to use the provider's configured default model.

## Important capability semantics

The OpenAI-compatible `/v1/models` response does not standardize model capability metadata. Therefore model-level capabilities are `Unknown` unless the backend supplies the optional `capabilities` extension described above. Adapter capabilities describe what this application's adapter can send; they are not claims about the underlying model.

## Integration

Replace the corresponding files from the streaming step:

- `Program.cs`
- `ModelContracts.cs`
- `OpenAiCompatibleProvider.cs`
- `UserTask.cs` (unchanged; included for a complete copy)
- `backend.csproj` (unchanged; included for reference)

Build:

```powershell
dotnet build .\backend.csproj -c Release
```
