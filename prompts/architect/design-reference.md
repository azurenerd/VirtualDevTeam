---
version: "1.0"
description: "Visual design reference context to append to the architect system prompt"
variables:
  - design_context
tags:
  - architect
  - design
---

## VISUAL DESIGN REFERENCE
The repository contains visual design reference files that define the exact UI layout. Your architecture MUST define components that map directly to the visual sections in this design. Include a '## UI Component Architecture' section in your output that maps each visual section from the design to a specific component, its CSS layout strategy, data bindings, and interactions.

{{design_context}}
