param(
    [string]$Prompt,
    [string]$OutPath,
    [string]$Size = "1024x1024"
)

$deployments = ($env:AZURE_OPENAI_IMAGE_DEPLOYMENTS -split ',').Where({$_})
$endpoint    = $env:AZURE_OPENAI_IMAGE_ENDPOINT.TrimEnd('/')
$apiVersion  = $env:AZURE_OPENAI_IMAGE_API_VERSION

$headers = @{ 'Content-Type' = 'application/json' }
if ($env:AZURE_OPENAI_IMAGE_API_KEY) {
    $headers['api-key'] = $env:AZURE_OPENAI_IMAGE_API_KEY
} elseif ($env:AZURE_OPENAI_IMAGE_BEARER) {
    $headers['Authorization'] = "Bearer $($env:AZURE_OPENAI_IMAGE_BEARER)"
} else { throw "No image-gen credential in env." }

New-Item -ItemType Directory -Path (Split-Path $OutPath) -Force | Out-Null

$body = @{
    prompt = $Prompt
    n = 1
    size = $Size
    quality = 'high'
    output_format = 'png'
} | ConvertTo-Json

$saved = $false
foreach ($deployment in $deployments) {
    $url = "$endpoint/openai/deployments/$deployment/images/generations?api-version=$apiVersion"
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $resp = Invoke-RestMethod -Uri $url -Method Post -Headers $headers -Body $body -ErrorAction Stop
            $b64  = $resp.data[0].b64_json
            if (-not $b64) { throw "API returned no b64_json payload" }
            [IO.File]::WriteAllBytes($OutPath, [Convert]::FromBase64String($b64))
            $bytes = [IO.File]::ReadAllBytes($OutPath)
            $isPng = $bytes.Length -ge 8 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47
            if ($isPng) {
                Write-Host "OK: $deployment attempt $attempt -> $OutPath ($($bytes.Length) bytes)"
                $saved = $true; break
            }
            Write-Host "WARN: not valid PNG - retrying"
        } catch {
            $msg = $_.Exception.Message
            Write-Host "ERR: $deployment attempt $attempt -> $msg"
            if ($msg -match '429|throttled|RateLimit') { Start-Sleep -Seconds (5 * $attempt) }
            if ($msg -match '404|NotFound|DeploymentNotFound') { break }
        }
    }
    if ($saved) { break }
}
if (-not $saved) { Write-Host "FAILED: All deployments exhausted for $OutPath"; exit 1 }
