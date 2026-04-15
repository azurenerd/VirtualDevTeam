---
version: "1.0"
description: "Multi-turn step 1 - identify key architectural decisions"
variables:
  - task_title
  - task_description
  - tech_stack
  - pm_spec
  - research
tags:
  - architect
  - multi-turn
---
I need you to design the system architecture for our project.

**Task:** {{task_title}}

**Description:** {{task_description}}

**Technology Stack (mandatory):** {{tech_stack}}

## PM Specification (Business Requirements)
{{pm_spec}}

## Research Findings
{{research}}

First, identify the key architectural decisions we need to make. For each decision, explain the options, trade-offs, and your recommendation. Ensure the architecture supports all business goals and user stories from the PM Spec. All decisions must use the mandatory technology stack specified above. List them clearly.
