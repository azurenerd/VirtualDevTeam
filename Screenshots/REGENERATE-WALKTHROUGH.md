# Regenerate Walkthrough GIFs & Summaries

> **Purpose**: This runbook allows any AI assistant (Copilot CLI, etc.) to regenerate all walkthrough animated GIFs and summaries from updated screenshots. Run this after updating dashboard screenshots.

## Prerequisites

- **ffmpeg** installed and on PATH
- Screenshots in this folder (`Screenshots/`) named with the convention below
- PowerShell (Windows)

## Screenshot Naming Convention

Files follow this pattern:
```
{GroupNumber} {DescriptiveName}_VDT.png           → Main image for a group
{GroupNumber}.{SubNumber} {DescriptiveName}_VDT.png → Sub-image within a group
```

**Examples:**
```
1 WelcomeView_VDT.png           → Group 1, first image
1.1 WelcomePrereqsView_VDT.png  → Group 1, sub-image 1
1.2 WelcomeAuthView_VDT.png     → Group 1, sub-image 2
3 DevelopProjectDescView_VDT.png → Group 3, single image
```

**Grouping rule**: Parse with regex `^(\d+)(?:\.(\d+))?\s` — integer group + optional decimal substep. Sort numerically (not lexicographic) to avoid `1.10` sorting before `1.2`.

## GIF Generation Script

Run this PowerShell script from the repo root:

```powershell
$screenshotsDir = "Screenshots"
$outputDir = "src/VirtualDevTeam.Dashboard/wwwroot/walkthrough"

# Ensure output directory exists
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

# Get all PNG files (cmd /c handles filenames with leading spaces)
$files = cmd /c "dir /b `"$screenshotsDir\*.png`"" 2>$null

# Group by leading number using ArrayList to avoid PS5 array unwrapping
$groups = @{}
foreach ($f in $files) {
    if ($f -match '^(\d+)(?:\.(\d+))?\s') {
        $groupNum = [int]$Matches[1]
        $subNum = if ($Matches[2]) { [int]$Matches[2] } else { 0 }
        if (-not $groups.ContainsKey($groupNum)) {
            $groups[$groupNum] = [System.Collections.ArrayList]::new()
        }
        $groups[$groupNum].Add([PSCustomObject]@{ File = $f; Sub = $subNum }) | Out-Null
    }
}

Write-Host "Found $($groups.Count) groups"

foreach ($groupNum in ($groups.Keys | Sort-Object)) {
    $items = @($groups[$groupNum] | Sort-Object Sub)
    $count = $items.Count
    $gifName = "walkthrough-{0:D2}.gif" -f $groupNum
    $gifPath = Join-Path $outputDir $gifName

    if ($count -eq 1) {
        # Single-frame: direct convert
        $srcPath = Join-Path $screenshotsDir $items[0].File
        $palettePath = Join-Path $env:TEMP "pal-wt-$groupNum.png"

        Start-Process ffmpeg -ArgumentList "-i `"$srcPath`" -vf `"scale=1280:-2:flags=lanczos,palettegen`" -update 1 -y `"$palettePath`"" -NoNewWindow -Wait -PassThru -RedirectStandardError "$env:TEMP\wt-p1-$groupNum.log" | Out-Null
        $p = Start-Process ffmpeg -ArgumentList "-framerate 1/3 -i `"$srcPath`" -i `"$palettePath`" -filter_complex `"scale=1280:-2:flags=lanczos,format=rgb24[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=5`" -loop 0 -y `"$gifPath`"" -NoNewWindow -Wait -PassThru -RedirectStandardError "$env:TEMP\wt-p2-$groupNum.log"

        if ($p.ExitCode -eq 0) {
            $kb = [math]::Round((Get-Item $gifPath).Length / 1KB)
            Write-Host "OK  group $groupNum (1 frame) -> $gifName (${kb}KB)"
        } else { Write-Host "FAIL group $groupNum" }

        Remove-Item $palettePath -ErrorAction SilentlyContinue
    } else {
        # Multi-frame: normalize dimensions then concat
        $tempDir = Join-Path $env:TEMP "wt-norm-$groupNum"
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

        # Get target dimensions from first image
        $firstSrc = Join-Path $screenshotsDir $items[0].File
        $probe = ffmpeg -i "$firstSrc" 2>&1 | Select-String "(\d{3,5})x(\d{3,5})"
        $origW = [int]$probe.Matches[0].Groups[1].Value
        $origH = [int]$probe.Matches[0].Groups[2].Value
        $targetW = 1280
        $targetH = [math]::Floor($origH * $targetW / $origW)
        if ($targetH % 2 -ne 0) { $targetH++ }

        # Pre-process: scale all frames to identical dimensions + rgb24 format
        for ($i = 0; $i -lt $count; $i++) {
            $srcPath = Join-Path $screenshotsDir $items[$i].File
            $framePath = Join-Path $tempDir ("frame_{0:D3}.png" -f $i)
            Start-Process ffmpeg -ArgumentList "-i `"$srcPath`" -vf `"scale=${targetW}:${targetH}:flags=lanczos,format=rgb24`" -y `"$framePath`"" -NoNewWindow -Wait -PassThru -RedirectStandardError "$env:TEMP\wt-pre-$groupNum-$i.log" | Out-Null
        }

        # Create concat file (CRITICAL: repeat last file for duration to work)
        $concatPath = Join-Path $env:TEMP "concat-wt-$groupNum.txt"
        $lines = @()
        for ($i = 0; $i -lt $count; $i++) {
            $fp = (Join-Path $tempDir ("frame_{0:D3}.png" -f $i)) -replace '\\','/'
            $lines += "file '$fp'"
            $lines += "duration 3"
        }
        # Repeat last frame (ffmpeg ignores duration of final file without this)
        $lastFp = (Join-Path $tempDir ("frame_{0:D3}.png" -f ($count - 1))) -replace '\\','/'
        $lines += "file '$lastFp'"
        $lines | Out-File -FilePath $concatPath -Encoding ascii

        $palettePath = Join-Path $env:TEMP "pal-wt-$groupNum.png"

        # Pass 1: palette
        Start-Process ffmpeg -ArgumentList "-f concat -safe 0 -i `"$concatPath`" -vf `"palettegen=stats_mode=diff`" -update 1 -y `"$palettePath`"" -NoNewWindow -Wait -PassThru -RedirectStandardError "$env:TEMP\wt-m1-$groupNum.log" | Out-Null

        # Pass 2: encode
        $p = Start-Process ffmpeg -ArgumentList "-f concat -safe 0 -i `"$concatPath`" -i `"$palettePath`" -filter_complex `"[0:v][1:v]paletteuse=dither=bayer:bayer_scale=5`" -loop 0 -y `"$gifPath`"" -NoNewWindow -Wait -PassThru -RedirectStandardError "$env:TEMP\wt-m2-$groupNum.log"

        if ($p.ExitCode -eq 0) {
            $kb = [math]::Round((Get-Item $gifPath).Length / 1KB)
            Write-Host "OK  group $groupNum ($count frames) -> $gifName (${kb}KB)"
        } else { Write-Host "FAIL group $groupNum" }

        Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $concatPath -ErrorAction SilentlyContinue
        Remove-Item $palettePath -ErrorAction SilentlyContinue
    }
}

# Validation
$gifs = Get-ChildItem "$outputDir\*.gif" -ErrorAction SilentlyContinue
Write-Host "`n=== Validation ==="
Write-Host "Total GIFs: $($gifs.Count)"
$totalMB = [math]::Round(($gifs | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "Total size: ${totalMB}MB"
$large = $gifs | Where-Object { $_.Length -gt 500KB }
if ($large) {
    Write-Host "WARNING: Large GIFs (>500KB):"
    $large | ForEach-Object { Write-Host "  $($_.Name) ($([math]::Round($_.Length/1KB))KB)" }
}
```

## Summary Content (walkthrough-content.json)

After generating GIFs, update `src/VirtualDevTeam.Dashboard/wwwroot/walkthrough/walkthrough-content.json`.

### JSON Schema
```json
{
  "sections": [
    {
      "step": 1,
      "title": "Short Page Title",
      "gif": "walkthrough-01.gif",
      "summary": "2-3 sentence concise summary. First-time user perspective. Highlight what makes this unique to VDT.",
      "chips": ["Chip1", "Chip2"]
    }
  ]
}
```

### Summary Writing Guidelines
- **Concise**: 2–3 sentences max — the user should breeze through the tour
- **First-time user perspective**: Explain what the page does and why it matters
- **Highlight VDT differentiators**: Call out what makes this feature unique vs. what users get elsewhere
- **Feature chips**: 2–3 max per section, representing genuinely notable capabilities
- **Refer to** `docs/VDT-KEY-DIFFERENTIATORS.md` for the full list of unique selling points to weave in

### Section-to-Page Mapping
| Group | Dashboard Page | Key Differentiators to Highlight |
|-------|---------------|--------------------------------|
| 1 | Welcome Wizard | Zero-config auth via CLI tools |
| 2 | Develop Auth | Multi-platform (GitHub + ADO) |
| 3 | Project Description | Natural language → spec generation |
| 4 | Clarifying Questions | Gap detection before building |
| 5 | Scenarios | Vision tracking throughout build |
| 6 | Reviews | Human gates + expert agent reviews |
| 7 | Work Items | Native platform work item generation |
| 8 | Review & Launch | One-click autonomous launch |
| 9 | Overview | Live status + pre-PR questions |
| 10 | Timeline | Full E2E phase visibility |
| 11 | Frameworks | Multi-candidate + AI judging + image gen |
| 12 | Reasoning | Decision transparency + reasoning trail |
| 13 | Repository | Unified view (code, docs, issues, PRs) |
| 14 | Scenarios | Scenario coverage + PR mapping |
| 15 | Team View | Team visualization (future: 3D) |
| 16 | Pipelines | CI/CD integration |
| 17 | Health Monitor | Stuck detection + auto-escalation |
| 18 | Metrics | Usage analytics + cost visibility |
| 19 | Configuration | Custom roles, prompts, MCP |
| 20 | Director CLI | Director + per-agent chat |
| 21 | Testing | Clone & run + Playwright media |
| 22 | Flow Monitor | Deadlock detection + diagnostics |

## Output Paths
- **GIFs**: `src/VirtualDevTeam.Dashboard/wwwroot/walkthrough/walkthrough-NN.gif`
- **Content**: `src/VirtualDevTeam.Dashboard/wwwroot/walkthrough/walkthrough-content.json`
- **Differentiators**: `docs/VDT-KEY-DIFFERENTIATORS.md`

## Validation Checklist
After regeneration, verify:
- [ ] GIF count matches screenshot group count
- [ ] JSON `sections` array has one entry per GIF
- [ ] Each JSON entry's `gif` field matches a file in wwwroot/walkthrough/
- [ ] No GIF exceeds 1MB (optimize if so: reduce resolution or colors)
- [ ] `dotnet build src/VirtualDevTeam.Dashboard` compiles without errors
- [ ] GIFs accessible at `http://localhost:5050/walkthrough/walkthrough-01.gif`
