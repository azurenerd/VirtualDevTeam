---
version: "1.0"
description: "User prompt for revising Architecture.md with current content and feedback"
variables:
  - current_content
  - feedback
tags:
  - architect
  - revision
---
## Current Architecture.md:

{{current_content}}

## Reviewer Feedback:

{{feedback}}

Revise the Architecture.md to address the feedback. Return the COMPLETE revised document.
