---
version: "1.0"
description: "Revision user prompt providing current document and reviewer feedback"
variables:
  - current_doc
  - feedback
---
## Current Research.md:

{{current_doc}}

## Reviewer Feedback:

{{feedback}}

Please revise the Research.md document to address the reviewer's feedback. Return the COMPLETE revised document (not just the changes).
