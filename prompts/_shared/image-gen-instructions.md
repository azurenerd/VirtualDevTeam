---
name: image-gen-instructions
description: How to generate images by calling the Azure OpenAI image-generation REST endpoint directly using credentials supplied in env vars. Replaces the prior MCP-wrapper approach (2026-05-12).
---

## Image Generation Capability — Direct REST

When the task requires producing a visual deliverable (sprite sheet, reference image, concept art, UI mockup, icon set), call the Azure OpenAI image-generation REST endpoint **directly** from your shell tool. There is NO MCP wrapper to wire in — the Runner injects credentials into your process environment so a single REST call works out of the box.

### Available environment variables

The Runner exports these into every CLI session when the project has image-gen configured:

| Variable | Meaning |
|---|---|
| `AZURE_OPENAI_IMAGE_ENDPOINT` | Resource base URL (no trailing slash). e.g. `https://my-img-resource.openai.azure.com` |
| `AZURE_OPENAI_IMAGE_API_VERSION` | API version. e.g. `2025-04-01-preview` |
| `AZURE_OPENAI_IMAGE_DEPLOYMENTS` | CSV of deployments to try in order (primary first). e.g. `gpt-image-2,gpt-image-1.5,gpt-image-1,gpt-image-1-mini` |
| `AZURE_OPENAI_IMAGE_API_KEY` | Static API key — present when auth method is ApiKey. |
| `AZURE_OPENAI_IMAGE_BEARER` | Fresh Entra bearer token — present when auth method is DefaultAzureCredential. ~1h TTL. |

Exactly ONE of `AZURE_OPENAI_IMAGE_API_KEY` or `AZURE_OPENAI_IMAGE_BEARER` is set. Use whichever is present.

**If `AZURE_OPENAI_IMAGE_ENDPOINT` is empty or unset, image gen is not configured for this project — stop and tell the operator instead of trying to fake an image.**

### Recipe (PowerShell — adapt for bash if needed)

```powershell
# 1. Detailed prompt — be SPECIFIC. ~400-1200 chars. Mention subject, style, perspective,
#    framing, color palette, background, transparency, and IP-safety clause.
$prompt = @'
A medieval-fantasy cannon tower for a stylized cartoon tower-defense game.
Top-down 3/4 isometric view, vibrant colors, clean read at small sizes.
Stone base with wood reinforcements; brass barrel with rivets; subtle weathering.
Transparent background (alpha channel). 1024x1024. Original art only — no
recognizable characters, logos, or trademarked elements.
'@

# 2. Walk the deployment fallback ladder. The CSV puts the highest-quality model first.
$deployments = ($env:AZURE_OPENAI_IMAGE_DEPLOYMENTS -split ',').Where({$_})
$endpoint    = $env:AZURE_OPENAI_IMAGE_ENDPOINT.TrimEnd('/')
$apiVersion  = $env:AZURE_OPENAI_IMAGE_API_VERSION
$outPath     = "C:\path\to\asset.png"   # absolute path - create parent dir first
New-Item -ItemType Directory -Path (Split-Path $outPath) -Force | Out-Null

$headers = @{ 'Content-Type' = 'application/json' }
if ($env:AZURE_OPENAI_IMAGE_API_KEY) {
    $headers['api-key'] = $env:AZURE_OPENAI_IMAGE_API_KEY
} elseif ($env:AZURE_OPENAI_IMAGE_BEARER) {
    $headers['Authorization'] = "Bearer $($env:AZURE_OPENAI_IMAGE_BEARER)"
} else { throw "No image-gen credential in env." }

$body = @{
    prompt = $prompt
    n = 1
    size = '1024x1024'
    quality = 'high'
} | ConvertTo-Json

$saved = $false
foreach ($deployment in $deployments) {
    $url = "$endpoint/openai/deployments/$deployment/images/generations?api-version=$apiVersion"
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $resp = Invoke-RestMethod -Uri $url -Method Post -Headers $headers -Body $body -ErrorAction Stop
            $b64  = $resp.data[0].b64_json
            if (-not $b64) { throw "API returned no b64_json payload" }
            [IO.File]::WriteAllBytes($outPath, [Convert]::FromBase64String($b64))
            # Verify the file is a real PNG (magic bytes 89 50 4E 47) — proves the API
            # returned image data, not a JSON error blob that was b64-decoded into garbage.
            # We deliberately do NOT check file size — legitimate icons can be 3 KB.
            $bytes = [IO.File]::ReadAllBytes($outPath)
            $isPng = $bytes.Length -ge 8 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 `
                     -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47
            if ($isPng) {
                Write-Host "OK: $deployment attempt $attempt -> $outPath ($($bytes.Length) bytes, valid PNG)"
                $saved = $true; break
            }
            Write-Host "WARN: $deployment attempt $attempt -> $outPath is not a valid PNG (first bytes: $($bytes[0..3])) - retrying"
        } catch {
            $msg = $_.Exception.Message
            Write-Host "ERR: $deployment attempt $attempt -> $msg"
            if ($msg -match '429|throttled|RateLimit') { Start-Sleep -Seconds (5 * $attempt) }
            # 404 / DeploymentNotFound / ResourceNotFound -> break inner loop, try next deployment
            if ($msg -match '404|NotFound|DeploymentNotFound') { break }
        }
    }
    if ($saved) { break }
}
if (-not $saved) { throw "All deployments + retries exhausted for $outPath" }
```

### Rules

1. **Walk the deployment ladder in order** — `gpt-image-2` first (highest quality), then `gpt-image-1.5`, `gpt-image-1`, `gpt-image-1-mini` last. On 429 / capacity throttle: retry up to 3 times with backoff, then move to the next deployment. On 404 / DeploymentNotFound: skip immediately to the next.
2. **Verify the saved file is a valid PNG** by checking the first 4 magic bytes (`89 50 4E 47`). Do NOT use file-size thresholds — legitimate small icons can be a few KB and large concept art can be > 1 MB. Size says nothing about correctness; the PNG signature is the universal truth-check.
3. **Best-effort behaviour**: if every deployment fails for a given asset, log the failure in your PR description and **continue** the rest of your task. Image generation is best-effort; it must not block code work.
4. **Cost awareness**: each successful call is roughly $0.04–$0.20 depending on model + quality. The PM declares the image budget in `[image-deliverables]` — never generate more than the manifest specifies.
5. **Style anchors**: when the PMSpec declares a master `style-anchor.png`, include its description in subsequent prompts (e.g. *"match the style, palette, perspective, line weight, and lighting of the established style anchor"*). The endpoint doesn't accept binary references — describe what you saw.
6. **Output paths must be absolute** and inside your assigned workspace. Never write outside it.
7. **NEVER fabricate PNGs from any other source.** Do not call Pillow, ImageMagick rectangles, `python -c "from PIL import Image..."`, ASCII art, or placeholder byte sequences as a fallback when the API call fails. If every deployment fails, leave the asset file ABSENT and surface the failure in the PR description — an absent asset is honest; a fabricated PNG looks like success to gates but is unusable. The judge / FlowMonitor / human reviewers can act on an honest gap; they cannot detect a convincing stub without opening the file.

### 🚀 Parallelization (CRITICAL — multi-asset tasks must batch)

A single image-gen call takes 20-60s. Sequential generation of N assets = N × 30s wall-clock. For tasks with > 3 image deliverables you **MUST** parallelize, or the task will dominate the run's total time.

**The wave model:**

1. **Build the dependency graph** before generating any image:
   - **Master / style-anchor frames** have NO dependencies → fully parallelizable
   - **Variant frames** that reference a master (e.g. walk-1 needs the goblin master to anchor the character) → wait for the master, then run all variants of that entity in parallel
   - **Composition / manifests / sprite sheets** → after all frames complete

2. **Run waves with bounded parallelism (max 8 concurrent calls)**. Azure OpenAI typically allows 8-12 concurrent image-gen requests per resource — beyond that you hit 429 throttling and waste wall-clock retrying. 8 is the sweet spot.

3. **Wait for the full wave to complete** before moving to the next wave. Within a wave, each call has its own independent retry loop (3 attempts per call); if any call exhausts retries, log the failure but **let the wave finish** — don't cancel siblings.

4. **Worked example — 4 entities × 8 frames each (32 images total):**

   | Sequential (BAD) | Wave model (GOOD) |
   |---|---|
   | 32 calls × 30s = **16 min** | Wave 1: 4 masters in parallel = ~30s |
   | | Wave 2: 16 variants in parallel (throttle 8) = ~60s |
   | | Wave 3: 12 variants in parallel (throttle 8) = ~60s |
   | | **Total: ~2.5 min** (6× speedup) |

5. **Implementation patterns** (use whichever fits your toolchain):

   ```powershell
   # PowerShell — built-in parallel ForEach
   $assets | ForEach-Object -Parallel {
       # ... call image API ...
   } -ThrottleLimit 8
   ```

   ```python
   # Python — concurrent.futures (PREFERRED for image-gen because it pairs
   # naturally with `requests` for the REST call)
   from concurrent.futures import ThreadPoolExecutor, as_completed
   with ThreadPoolExecutor(max_workers=8) as ex:
       futures = {ex.submit(generate_image, asset): asset for asset in batch}
       for fut in as_completed(futures):
           result = fut.result()  # per-call retry already handled inside generate_image
   ```

   ```bash
   # Bash — xargs -P
   printf '%s\n' "${assets[@]}" | xargs -P 8 -I {} ./generate.sh {}
   ```

6. **Don't parallelize where dependencies forbid it.** Pass-2 variants need their Pass-1 master committed to disk first because the prompt embeds a description of the master's pose/palette/style. Within a single entity: master FIRST (sequential), then variants (parallel). Across entities: masters can run as one parallel wave, then per-entity variant waves can interleave.

7. **Surface progress as you go.** Print one line per completed asset (`✓ goblin/walk-2.png 71 KB OK` / `✗ orc/die-1.png deployment-ladder exhausted`) so the operator can watch progress in real time. Don't silently batch and announce only at the end.

8. **Failure handling within a wave**: if one call exhausts its retries, log the failure with the asset name + the last error message, but continue the rest of the wave. After the full task completes, list missing assets in the PR description so a human knows exactly what to re-run manually.

### ⚠️ Existing scripts in the workspace are NOT trusted

If you find an existing `generate-*.ps1` / `generate-*.py` / similar image-gen helper script in the workspace (under `art-pipeline/`, `scripts/`, `tools/`, etc.), **DO NOT trust its auth or endpoint logic**. Previous Artist runs have committed scripts with subtle auth bugs (e.g., key-length-based heuristics for `Bearer` vs `api-key`) that produce silent 401 → System.Drawing fallback failures. The recipe in this prompt is the single source of truth.

When reusing an existing script:
1. Verify the auth header logic matches THIS recipe exactly (`api-key` for `AZURE_OPENAI_IMAGE_API_KEY`, `Bearer` ONLY for `AZURE_OPENAI_IMAGE_BEARER` — never key-length based).
2. Verify the endpoint URL pattern matches: `{endpoint}/openai/deployments/{deployment}/images/generations?api-version={version}`.
3. Verify the PNG signature check is byte-based (`89 50 4E 47`), not size-based.
4. If ANY of these is wrong, REPLACE the script entirely with the recipe above. Don't try to surgically fix — buggy auth scripts are the most common cause of "look like success but produce stubs" failures.



