---
version: "1.0"
description: "User prompt providing task context for pre-publish self-assessment"
variables:
  - issue_title
  - issue_body
  - changed_files
  - workspace_path
  - pr_number
  - attempt
  - implementation_context
  - previous_gaps
tags:
  - engineer
  - engineer-base
  - self-assessment
---
## Task Being Assessed

**PR #{{pr_number}}: {{issue_title}}**

## Original Requirements (from Issue)

{{issue_body}}

## Files Changed in This PR

{{changed_files}}

## Workspace Location

The code is at: `{{workspace_path}}`

Use your tools to read any of the changed files to verify their content against the requirements above.

## Implementation Context

The following notes capture key decisions, constraints, and events from the implementation phase. Use these to understand WHY certain choices were made — do not flag as gaps anything that was an intentional decision or a known constraint.

{{implementation_context}}

## Assessment Attempt

This is attempt **{{attempt}}** of the self-assessment.

{{previous_gaps}}

Please review the implementation against the original requirements and provide your assessment as JSON.
