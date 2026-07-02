---
version: "3.0"
description: "User prompt for CLI edit-based document revision"
variables:
  - doc_name
  - feedback
tags:
  - pm
  - revision
---
## Reviewer Feedback:

{{feedback}}

Edit the file `{{doc_name}}` in your working directory to address ONLY the feedback above.
Make minimal, surgical changes. Do not rewrite the whole file.
