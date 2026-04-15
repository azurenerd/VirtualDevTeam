---
version: "1.0"
description: "Design context section for PM system prompt"
variables:
  - design_context
tags:
  - pm
  - design
---


## CRITICAL: VISUAL DESIGN REFERENCE
The repository contains visual design reference files that define the EXACT UI to be built. You MUST:
1. Include a '## Visual Design Specification' section in PMSpec describing every visual element
2. Include a '## UI Interaction Scenarios' section with numbered scenarios (e.g., 'Scenario 1: User hovers over a milestone diamond and sees a tooltip with date and status')
3. Describe the exact layout: sections, grid structure, color scheme, typography
4. Reference the design file by name so engineers can consult it
5. Each user story that involves UI MUST reference the specific visual section from the design

{{design_context}}
