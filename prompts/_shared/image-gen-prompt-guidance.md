---
name: image-gen-prompt-guidance
description: How to write effective prompts for the generate_image tool. Subtle effects need strong cues.
---

## Writing Strong Image Prompts

Modern image-generation models (gpt-image-1/1.5/2) render **dramatic visuals reliably** but **silently skip subtle effects** when prompts use soft language. To get the result you want, follow these rules:

### Structure every prompt with these sections

1. **Subject** — what the image IS (one sentence, very concrete: "A single Cannon Turret tower with stone base and metal barrel, centered in frame.")
2. **Style** — visual language ("Bright saturated 2D top-down slightly-isometric tactical-diorama style, chunky readable silhouettes, soft cel-shading, polished mobile-game aesthetic, 12-color limited palette.")
3. **Perspective + framing** — camera angle, what's in/out of frame ("Centered, top-down 3/4 view, fits within the frame with ~10% margin on all sides.")
4. **Color palette** — explicit colors when possible ("Gray stone base, dark steel barrel, copper trim accents, yellow muzzle highlights, deep magenta #FF00FF background for chroma-key transparency.")
5. **Background / surroundings** — what fills the rest of the canvas ("Solid uniform magenta #FF00FF background filling every pixel that is not the subject. NO scenery, NO ground texture, NO shadow on the ground.")
6. **What it MUST NOT contain** — IP-safe guardrails ("Pure original asset. No recognizable IP, characters, logos, or copyrighted material from any commercial game.")

### Emphasize subtle effects with strong cues

This is the most common quality issue and the reason a charge-frame "faint glow" is rendered as nothing. **If you want a soft effect, ask for a dramatic one.**

| If you want… | Write this instead |
|---|---|
| "a glow at the barrel tip" | "a bright orange-yellow charged plasma orb glowing inside the barrel mouth, with a lens-flare halo and electrical sparks dancing on the rim" |
| "a slight recoil" | "the barrel slammed fully back into the recoil pose at maximum displacement, the base visibly absorbing the kickback" |
| "a small smoke puff" | "a dense gray smoke cloud rising 1/4 of the frame height from the barrel tip, dissipating outward, with visible smoke wisps and motion lines" |
| "subtle lighting" | "strong directional lighting from the upper-left at 45 degrees, casting a clear hard-edged shadow on the lower-right side of the subject" |

### Sprite-sheet specific rules

When you generate a multi-frame sprite sheet in one call:

- **Repeat consistency clauses for every frame.** "The tower BASE, POSITION, SCALE, LIGHTING DIRECTION, and PALETTE must be IDENTICAL across all N frames. ONLY the barrel pose and muzzle effect change."
- **Use clear frame boundaries.** "Layout: 2x2 grid (4 frames total), each frame exactly 512x512 pixels, separated by thin black gridlines."
- **List each frame's exact action verbatim.** Numbered 1, 2, 3, 4 with one specific change per frame.
- **Prefer the two-pass approach for character-critical assets.** Generate ONE master frame at 1024×1024 first, then call `generate_image` again with `reference_image_path` pointing at the master to generate each variant separately. This locks consistency more reliably than asking for an N-frame grid in a single shot.

### Reference images and style anchors

When `reference_image_path` is provided:
- Treat the reference image as the ground truth for style (palette, line weight, perspective, cel-shading depth).
- The new image must MATCH the reference's visual language. Restate that in the prompt: "Match the style, palette, line weight, and perspective of the provided reference image exactly. The new asset should look like it belongs in the same game and was drawn by the same artist."
- The new image's *subject* can differ from the reference (e.g., reference shows a tower, new image shows an enemy) — but the *style* must be identical.

### Length and detail

Aim for **400–1200 characters per prompt**. Below 400 risks ambiguity; above 1200 risks the model losing track of which clauses matter most. If a prompt grows past 1200, split into two passes (generate a master, then variants with reference).
