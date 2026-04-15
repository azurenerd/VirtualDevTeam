---
version: "1.0"
description: "Single-pass PMSpec generation prompt"
variables:
  - project_name
  - project_description
  - research_doc
  - design_sections
tags:
  - pm
  - spec
  - single-pass
---
I need you to create a PM Specification for our project.

**Project Name:** {{project_name}}

**Project Description:**
{{project_description}}

## Research Findings
{{research_doc}}

Produce a complete, structured PMSpec.md document with ALL of these sections:

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

Use these exact section headers. Be thorough, specific, and business-focused. Each user story must have clear acceptance criteria. This document will be the single source of truth for business requirements.
