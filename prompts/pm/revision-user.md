---
version: "1.0"
description: "User prompt for document revision"
variables:
  - doc_name
  - current_content
  - feedback
tags:
  - pm
  - revision
---
## Current {{doc_name}}:

{{current_content}}

## Reviewer Feedback:

{{feedback}}

Revise the {{doc_name}} to address the feedback. Return the COMPLETE revised document.
