$gen = "C:\Git\VirtualDevTeam\client\public\assets\sprites\_generate.ps1"
$base = "C:\Git\VirtualDevTeam\client\public\assets\sprites"

$styleDesc = "Bright, saturated, toy-like tactical diorama art style. Slightly isometric top-down 3/4 view. Chunky readable silhouettes, polished cartoon aesthetic like Fieldrunners. Vibrant colors, clean edges, transparent background (alpha channel). Original art only - no recognizable characters, logos, or trademarked elements."

# ============ WAVE 1: Master frames (4 entities x 2 animations = 8 masters) ============
$masters = @(
    @{ Name="cannon-tower/_master-idle"; Prompt="A medieval-fantasy cannon tower for a stylized cartoon tower-defense game. $styleDesc Top-down 3/4 isometric view. Massive stone and iron cannon tower with a thick brass barrel, riveted iron bands, stone brick base with moss patches. The cannon barrel has a BRIGHT ORANGE-RED glowing ember inside the barrel mouth, radiating heat shimmer. Weathered but powerful appearance. 1024x1024 centered on transparent background." },
    @{ Name="cannon-tower/_master-fire"; Prompt="A medieval-fantasy cannon tower FIRING its cannon for a tower-defense game. $styleDesc Top-down 3/4 isometric view. Same stone and iron cannon tower - MASSIVE BRIGHT YELLOW-ORANGE MUZZLE FLASH erupting from the barrel, billowing smoke clouds, bright sparks and flame particles shooting outward, recoil visible in the barrel position. Explosive dramatic firing moment. 1024x1024 centered on transparent background." },
    @{ Name="archer-tower/_master-idle"; Prompt="A medieval-fantasy archer tower for a stylized cartoon tower-defense game. $styleDesc Top-down 3/4 isometric view. Tall wooden watchtower with a pointed shingled roof, wooden railings, and a hooded archer figure standing at the top with a large longbow at rest. Quiver of arrows visible on the archer's back. BRIGHT GOLDEN LANTERN glowing warmly at the tower peak. 1024x1024 centered on transparent background." },
    @{ Name="archer-tower/_master-fire"; Prompt="A medieval-fantasy archer tower SHOOTING an arrow for a tower-defense game. $styleDesc Top-down 3/4 isometric view. Same wooden watchtower - the hooded archer is drawing back the longbow with a BRIGHT GLOWING ENCHANTED ARROW, bow fully drawn, BRILLIANT CYAN-WHITE magical energy trail streaming from the arrowhead, intense focused pose. Dynamic shooting moment. 1024x1024 centered on transparent background." },
    @{ Name="goblin/_master-walk"; Prompt="A small goblin enemy creature for a stylized cartoon tower-defense game. $styleDesc Top-down 3/4 isometric view. Short green-skinned goblin with oversized pointed ears, wearing ragged leather armor and a tiny rusty sword. BRIGHT YELLOW glowing eyes, menacing grin. Mid-stride walking pose facing right. Exaggerated proportions - big head, small body. 1024x1024 centered on transparent background." },
    @{ Name="goblin/_master-die"; Prompt="A small goblin enemy creature defeated/dying for a tower-defense game. $styleDesc Top-down 3/4 isometric view. Same green-skinned goblin with pointed ears - knocked backward, arms flung out, BRIGHT RED-ORANGE impact sparks and stars circling its head, eyes replaced with cartoon X marks, sword flying away. Dramatic cartoon defeat pose. 1024x1024 centered on transparent background." },
    @{ Name="orc/_master-walk"; Prompt="A large muscular orc warrior enemy for a stylized cartoon tower-defense game. $styleDesc Top-down 3/4 isometric view. Massive green-skinned orc with heavy plate armor, wielding a huge spiked mace. BRIGHT RED WAR PAINT streaks across face, GLOWING ORANGE eyes burning with rage. Tusks protruding from lower jaw. Heavy stomping walk pose facing right. 1024x1024 centered on transparent background." },
    @{ Name="orc/_master-die"; Prompt="A large muscular orc warrior defeated/dying for a tower-defense game. $styleDesc Top-down 3/4 isometric view. Same massive green-skinned orc in plate armor - crumbling to knees, mace dropped, BRIGHT BLUE-WHITE ethereal energy wisps escaping from cracks in armor, eyes dimming, dramatic defeat collapse pose. Impact crater beneath. 1024x1024 centered on transparent background." }
)

Write-Host "=== WAVE 1: Generating 8 master frames (parallel, throttle 8) ==="
$masters | ForEach-Object -Parallel {
    $item = $_
    $outPath = "$using:base\$($item.Name).png"
    & $using:gen -Prompt $item.Prompt -OutPath $outPath
} -ThrottleLimit 8

Write-Host "`n=== WAVE 1 COMPLETE ==="

# ============ WAVE 2: Variant frames for all entities ============
$variants = @()

# Cannon Tower idle variants (frames 0-3)
foreach ($i in 0..3) {
    $poses = @(
        "barrel pointing straight ahead, ember glowing steadily inside barrel mouth",
        "barrel tilted very slightly left, ember pulsing BRIGHTER with orange-yellow heat waves",
        "barrel pointing straight ahead, BRIGHT SPARK flickering at barrel tip, subtle smoke wisp rising",
        "barrel tilted very slightly right, ember dimming slightly then FLARING back to bright orange"
    )
    $variants += @{ Name="cannon-tower/idle-$i"; Prompt="A medieval-fantasy cannon tower for a tower-defense game, frame $($i+1) of 4 idle animation. $styleDesc Top-down 3/4 isometric view. Stone and iron cannon tower with brass barrel, riveted bands, stone base. Idle pose variation: $($poses[$i]). Match the established style anchor exactly - same proportions, same color palette, same perspective. 256x256 sprite frame on transparent background." }
}

# Cannon Tower fire variants (frames 0-2)
$firePoses = @(
    "cannon barrel RECOILING backward, MASSIVE BRIGHT YELLOW-ORANGE MUZZLE BLAST erupting forward, thick smoke ring expanding, bright white-hot sparks showering outward",
    "cannon barrel at maximum recoil, BILLOWING DARK SMOKE CLOUD with BRIGHT ORANGE FIRE CORE still visible through smoke, debris particles flying, shockwave ring visible",
    "cannon barrel returning forward to rest position, smoke dissipating into wisps, last few BRIGHT EMBER SPARKS floating upward and fading, barrel tip still glowing red-hot"
)
foreach ($i in 0..2) {
    $variants += @{ Name="cannon-tower/fire-$i"; Prompt="A medieval-fantasy cannon tower FIRING for a tower-defense game, frame $($i+1) of 3 firing animation. $styleDesc Top-down 3/4 isometric view. Stone and iron cannon tower - $($firePoses[$i]). Match the established style anchor exactly. 256x256 sprite frame on transparent background." }
}

# Archer Tower idle variants (frames 0-3)
foreach ($i in 0..3) {
    $poses = @(
        "archer standing alert with bow at rest by side, scanning horizon, golden lantern glowing steadily",
        "archer shifting weight, bow hand adjusting grip, lantern FLICKERS casting dancing shadows",
        "archer pulling arrow from quiver on back, preparing, lantern glowing BRIGHT WARM GOLD",
        "archer returning to alert stance, arrow loosely nocked but not drawn, lantern steady warm glow"
    )
    $variants += @{ Name="archer-tower/idle-$i"; Prompt="A medieval-fantasy archer tower for a tower-defense game, frame $($i+1) of 4 idle animation. $styleDesc Top-down 3/4 isometric view. Wooden watchtower with pointed roof, hooded archer at top. Idle pose: $($poses[$i]). Match established style anchor. 256x256 sprite frame on transparent background." }
}

# Archer Tower fire variants (frames 0-2)
$firePoses = @(
    "archer drawing bowstring back fully, BRIGHT CYAN-WHITE ENCHANTED ARROW blazing with magical energy, concentrated pose, energy particles swirling around arrowhead",
    "archer RELEASING the arrow, BRILLIANT STREAK OF CYAN-WHITE LIGHT launching forward, bowstring vibrating visibly, magical energy burst at release point, archer leaning into the shot",
    "archer follow-through pose, bow arm extended, fading CYAN ENERGY TRAIL where arrow departed, residual sparkles floating, returning to ready stance"
)
foreach ($i in 0..2) {
    $variants += @{ Name="archer-tower/fire-$i"; Prompt="A medieval-fantasy archer tower SHOOTING for a tower-defense game, frame $($i+1) of 3 firing animation. $styleDesc Top-down 3/4 isometric view. Wooden watchtower with hooded archer - $($firePoses[$i]). Match established style anchor. 256x256 sprite frame on transparent background." }
}

# Goblin walk variants (frames 0-5)
$walkPoses = @(
    "right foot forward, left foot back, sword raised slightly, body leaning forward into stride",
    "mid-stride, both feet close together, sword swinging at side, ears bouncing",
    "left foot forward, right foot pushing off ground, sword trailing behind, ears flapping back",
    "full stride, left foot planted, right foot lifting high, sword pointing forward aggressively",
    "right foot coming down, body bobbing upward at apex of step, sword overhead briefly",
    "transitioning back to start, weight shifting, sword returning to side position, sneering expression"
)
foreach ($i in 0..5) {
    $variants += @{ Name="goblin/walk-$i"; Prompt="A small goblin enemy for a tower-defense game, frame $($i+1) of 6 walk cycle. $styleDesc Top-down 3/4 isometric view. Short green-skinned goblin with pointed ears, ragged leather armor, rusty sword, BRIGHT YELLOW glowing eyes. Walk pose: $($walkPoses[$i]). Facing right. Match established style anchor. 256x256 sprite frame on transparent background." }
}

# Goblin die variants (frames 0-2)
$diePoses = @(
    "goblin hit by impact, lurching backward, BRIGHT RED-ORANGE IMPACT FLASH at chest, eyes wide with shock, sword slipping from grip",
    "goblin tumbling backward through air, limbs splayed, BRIGHT YELLOW STARS and SPARKS circling head, eyes becoming X marks, sword flying away to the side",
    "goblin flat on ground defeated, limbs spread out, cartoon X-mark eyes, FADING STARS above head, tiny ghost wisps rising from body, sword on ground nearby"
)
foreach ($i in 0..2) {
    $variants += @{ Name="goblin/die-$i"; Prompt="A small goblin enemy dying in a tower-defense game, frame $($i+1) of 3 death animation. $styleDesc Top-down 3/4 isometric view. Green-skinned goblin with pointed ears - $($diePoses[$i]). Match established style anchor. 256x256 sprite frame on transparent background." }
}

# Orc walk variants (frames 0-5)
$walkPoses = @(
    "right foot stomping forward heavily, mace raised at side, ground cracking slightly under weight, red war paint vivid",
    "mid-stride, massive body shifting weight, mace swinging forward with momentum, armor plates clanking",
    "left foot forward in heavy stomp, mace trailing behind, tusks prominent, GLOWING ORANGE EYES blazing",
    "full powerful stride, ground impact dust cloud at feet, mace held high threateningly, muscles bulging",
    "right foot lifting for next step, body at full height, mace pointing forward, war paint streaks glowing",
    "weight transferring, slight forward lean, mace coming back to rest position, heavy breathing visible as steam puffs"
)
foreach ($i in 0..5) {
    $variants += @{ Name="orc/walk-$i"; Prompt="A large muscular orc warrior for a tower-defense game, frame $($i+1) of 6 walk cycle. $styleDesc Top-down 3/4 isometric view. Massive green-skinned orc in heavy plate armor, spiked mace, BRIGHT RED war paint, GLOWING ORANGE eyes, tusks. Walk pose: $($walkPoses[$i]). Facing right. Match established style anchor. 256x256 sprite frame on transparent background." }
}

# Orc die variants (frames 0-2)
$diePoses = @(
    "orc staggering from massive hit, BRIGHT BLUE-WHITE ENERGY EXPLOSION at chest, armor cracking with light beams shooting through cracks, mace slipping, expression of shock",
    "orc falling to knees, armor shattering into pieces, BRIGHT ETHEREAL BLUE WISPS streaming upward from body, mace hitting ground with impact dust, eyes dimming from orange to dark",
    "orc collapsed on ground in pile of broken armor, FADING BLUE-WHITE SPIRIT ENERGY dissipating upward, mace beside fallen body, peaceful defeated pose, small impact crater beneath"
)
foreach ($i in 0..2) {
    $variants += @{ Name="orc/die-$i"; Prompt="A large muscular orc warrior dying in a tower-defense game, frame $($i+1) of 3 death animation. $styleDesc Top-down 3/4 isometric view. Massive green-skinned orc in plate armor - $($diePoses[$i]). Match established style anchor. 256x256 sprite frame on transparent background." }
}

Write-Host "`n=== WAVE 2: Generating $($variants.Count) variant frames (parallel, throttle 8) ==="
$variants | ForEach-Object -Parallel {
    $item = $_
    $outPath = "$using:base\$($item.Name).png"
    & $using:gen -Prompt $item.Prompt -OutPath $outPath -Size "1024x1024"
} -ThrottleLimit 8

Write-Host "`n=== WAVE 2 COMPLETE ==="
Write-Host "`n=== Verifying all PNGs ==="

$allFiles = Get-ChildItem "$base\*\*.png" -Recurse | Where-Object { $_.Name -notlike '_*' }
$valid = 0; $invalid = 0; $missing = @()
foreach ($f in $allFiles) {
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    $isPng = $bytes.Length -ge 8 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47
    if ($isPng) { $valid++; Write-Host "  OK $($f.Name) ($($bytes.Length) bytes)" }
    else { $invalid++; Write-Host "  FAIL $($f.FullName)" }
}

# Check expected files
$expected = @()
foreach ($i in 0..3) { $expected += "cannon-tower\idle-$i.png" }
foreach ($i in 0..2) { $expected += "cannon-tower\fire-$i.png" }
foreach ($i in 0..3) { $expected += "archer-tower\idle-$i.png" }
foreach ($i in 0..2) { $expected += "archer-tower\fire-$i.png" }
foreach ($i in 0..5) { $expected += "goblin\walk-$i.png" }
foreach ($i in 0..2) { $expected += "goblin\die-$i.png" }
foreach ($i in 0..5) { $expected += "orc\walk-$i.png" }
foreach ($i in 0..2) { $expected += "orc\die-$i.png" }

foreach ($e in $expected) {
    if (-not (Test-Path "$base\$e")) { $missing += $e }
}

Write-Host "`nValid: $valid, Invalid: $invalid, Missing: $($missing.Count)"
if ($missing.Count -gt 0) { Write-Host "Missing files:"; $missing | ForEach-Object { Write-Host "  $_" } }
