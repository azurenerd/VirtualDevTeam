---
version: "1.0"
description: "Turn 2: Compile structured PMSpec document"
variables:
  - project_name
  - design_sections
tags:
  - pm
  - spec
  - multi-turn
---
Now compile everything into a single, structured PMSpec.md document with these exact sections:

# PM Specification: {{project_name}}

## Executive Summary
(2-3 sentences describing what we're building and why)

## Business Goals
(Numbered list of concrete business objectives)

## User Stories & Acceptance Criteria
(Each story as: **As a [role]**, I want [capability], so that [benefit]. Followed by acceptance criteria as a checklist. For UI stories, reference the specific visual section from the design file.)

{{design_sections}}## Scope
### In Scope
(Bullet list)
### Out of Scope
(Bullet list — explicit exclusions to prevent scope creep)

## Non-Functional Requirements
(Performance targets, security requirements, scalability needs, reliability SLAs)

## Success Metrics
(Measurable criteria for project completion)

## Constraints & Assumptions
(Technical constraints, timeline assumptions, dependency assumptions)

Replace {ProjectName} with '{{project_name}}'. Use these exact section headers. This document will be the single source of truth for business requirements.
