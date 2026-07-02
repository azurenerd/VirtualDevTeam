---
version: "1.1"
description: "Multi-turn step 1 - identify key architectural decisions"
variables:
  - task_title
  - task_description
  - tech_stack
  - pm_spec
  - research
  - unanswered_decisions
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
{{unanswered_decisions}}
First, identify the key architectural decisions we need to make. For each decision, explain the options, trade-offs, and your recommendation. Ensure the architecture supports all business goals and user stories from the PM Spec. All decisions must use the mandatory technology stack specified above.

For each decision you make, report it in this exact format so it can be tracked:

DECISION: [short descriptive title]
CHOICE: [what you decided]
RATIONALE: [why — trade-offs and alternatives rejected]
IMPACT: [XS|S|M|L|XL]

List all decisions clearly with their DECISION blocks.
