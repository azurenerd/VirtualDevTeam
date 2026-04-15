---
version: "1.0"
description: "PE SME expertise assessment prompt"
variables:
  - task_description
tags:
  - principal-engineer
  - sme
---
Evaluate whether this engineering task requires specialist expertise beyond what a general Principal/Senior Engineer can handle. Consider: security, databases, ML/AI, compliance, specific cloud services, accessibility, etc.

Task: {{task_description}}

Respond with ONLY "YES" or "NO" on the first line.
If YES, on the second line list 2-3 required capability keywords (comma-separated).
