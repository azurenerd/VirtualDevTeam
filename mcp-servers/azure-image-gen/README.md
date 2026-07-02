# @vdt/azure-image-gen-mcp

A Model Context Protocol (MCP) server that exposes **Azure OpenAI image generation** (`gpt-image-*` family) as a single `generate_image` tool, with automatic deployment fallback and prompt-refinement retry.

> This server is consumed by the **VirtualDevTeam (VDT)** runner and by Copilot CLI agentic sessions. It is **not designed for standalone use** — agent-facing concerns like cost tracking, semantic vision verification, dashboard telemetry, and the tool allowlist live in the .NET host that spawns this server. Keep this MCP layer thin.

## What it does

- **One tool**: `generate_image` — POST to `{endpoint}/openai/deployments/{deployment}/images/generations` and write the resulting PNG to a caller-specified absolute path.
- **Deployment fallback**: tries deployments in priority order. On `429`, `503`, or error codes `EngineOverloaded`/`ServiceUnavailable`/`ResourceNotFound`/`DeploymentNotFound`, falls through to the next deployment. Hard errors (400/401/etc.) abort.
- **Prompt-refinement retry**: on attempt 2+ the prompt is sharpened (rule-based, no LLM call) before retrying. Up to `max_attempts` times.
- **Structural verification**: confirms output is a real PNG ≥ 5 KB. (Semantic vision check is intentionally out of scope — handled by the .NET host.)

## Install

```bash
cd mcp-servers/azure-image-gen
npm install
npm run build
```

Requires **Node 20+**.

## Configure (env vars)

| Variable | Required | Default | Description |
|---|---|---|---|
| `AZURE_IMAGE_ENDPOINT` | yes | — | e.g. `https://my-aoai.openai.azure.com/` |
| `AZURE_IMAGE_DEPLOYMENTS_JSON` | yes | — | JSON array of deployment names in priority order, e.g. `["gpt-image-2","gpt-image-1","gpt-image-1-mini"]` |
| `AZURE_IMAGE_API_VERSION` | no | `2025-04-01-preview` | API version |
| `AZURE_IMAGE_AUTH_METHOD` | no | `DefaultAzureCredential` | `DefaultAzureCredential` (preferred, keyless) or `ApiKey` |
| `AZURE_IMAGE_API_KEY` | when `AUTH_METHOD=ApiKey` | — | the key |

When `AUTH_METHOD=DefaultAzureCredential`, the server acquires tokens for scope `https://cognitiveservices.azure.com/.default` (cached + auto-refreshed near expiry). Sign in via `az login` or rely on managed identity.

## Add to `mcp.json` / VDT `appsettings.json`

VDT registers MCP servers under the `VirtualDevTeam:McpServers` section. Drop in:

```jsonc
"azure-image-gen": {
  "Name": "azure-image-gen",
  "Description": "Azure OpenAI gpt-image-* — single-tool image generation with deployment fallback",
  "Command": "node",
  "Args": ["C:\\Git\\VirtualDevTeam\\mcp-servers\\azure-image-gen\\dist\\index.js"],
  "Transport": "Stdio",
  "RequiredRuntimes": ["node"],
  "ProvidedCapabilities": ["image-generation"],
  "AllowedTools": ["generate_image"],
  "Env": {
    "AZURE_IMAGE_ENDPOINT": "https://behumphr-imgen-65518.openai.azure.com/",
    "AZURE_IMAGE_DEPLOYMENTS_JSON": "[\"gpt-image-2\",\"gpt-image-1\",\"gpt-image-1-mini\"]",
    "AZURE_IMAGE_API_VERSION": "2025-04-01-preview",
    "AZURE_IMAGE_AUTH_METHOD": "ApiKey",
    "AZURE_IMAGE_API_KEY": "${VirtualDevTeam:AzureOpenAI:ImageApiKey}"
  }
}
```

The `AllowedTools` list constrains which tools are visible to agents; in this case the only tool is `generate_image`.

## Example tool call

A Copilot CLI / agent session invokes the tool over MCP like so:

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "generate_image",
    "arguments": {
      "prompt": "An isometric pixel-art hamster scientist holding a glowing vial of bright cyan plasma. Dramatic neon rim-lighting, sparks, motion lines. 1980s arcade aesthetic.",
      "size": "1024x1024",
      "output_path": "C:\\Git\\VirtualDevTeam\\.agents\\generated\\hamster-scientist.png",
      "max_attempts": 3
    }
  }
}
```

Response includes the absolute output path, byte count, which deployment served the request, and the full attempt log (useful for diagnosing fallback behavior).

## Testing

```bash
npm test
```

Runs three mocked tests for the fallback chain (primary succeeds, primary 429→fallback, all fail) plus a hard-failure short-circuit test. Uses Node's built-in `node:test`; no extra runner dep.

## Design notes / TODOs

- **No vision-AI verification here.** v1 only checks `>= 5 KB` + PNG magic bytes. Semantic verification (does the image actually depict what the prompt asked for?) belongs in the .NET host where the broader multi-modal model registry is already wired up.
- **No cost tracking.** Each call is one image; cost is uniform per deployment. The .NET runner counts MCP tool invocations.
- **No telemetry / SignalR.** This server writes to stderr only. The .NET runner observes the subprocess output and bridges to the dashboard.
- **Reference image** is currently hinted into the prompt text. True image-conditioning on the gpt-image-* endpoint is not yet stable; when it ships we'll extend `generateImage` without changing the public tool contract.
- **Output is always PNG.** The Azure endpoint can emit other formats but we want a single deterministic on-disk artifact for downstream agents.
