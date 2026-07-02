---
version: "1.1"
description: "Single-pass user prompt for complete architecture design in one turn"
variables:
  - task_title
  - task_description
  - tech_stack
  - pm_spec
  - research
  - unanswered_decisions
  - existing_project_context
tags:
  - architect
  - single-pass
---
I need you to design the complete system architecture for our project.

**Task:** {{task_title}}

**Description:** {{task_description}}

**Technology Stack (mandatory):** {{tech_stack}}

## PM Specification (Business Requirements)
{{pm_spec}}

## Research Findings
{{research}}
{{unanswered_decisions}}
{{#existing_project_context}}
## Existing Project Context
This is a feature for an EXISTING project. The following summary describes the current codebase, architecture, and conventions. Your architecture design MUST integrate with the existing structure — extend it, don't replace it. Reference existing components, patterns, and file organization.

{{existing_project_context}}
{{/existing_project_context}}
Produce a complete, structured Architecture.md document with ALL of these sections:

# Architecture

## Overview & Goals
(High-level summary)

## System Components
(Each component with responsibilities, interfaces, dependencies, data)

## Component Interactions
(Data flow and communication patterns)

## Data Model
(Entities, relationships, storage)

## API Contracts
(Endpoints, request/response shapes, error handling)

## Infrastructure Requirements
(Hosting, networking, storage, CI/CD)

## Technology Stack Decisions
(Chosen technologies with justification)

## Security Considerations
(Auth, data protection, validation)

## Scaling Strategy
(How the system scales)

## Risks & Mitigations
(Key risks and how to address them)

Use these exact section headers. Be thorough and specific. All decisions must use the mandatory technology stack. This document will be the single source of truth for the engineering team.
