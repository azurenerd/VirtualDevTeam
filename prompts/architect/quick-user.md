---
version: "1.0"
description: "User prompt for quick-mode minimal architecture document"
variables:
  - project_description
  - tech_stack
tags:
  - architect
  - quick-mode
---
Project: {{project_description}}
Tech Stack: {{tech_stack}}

Write a concise architecture document with these sections (1-2 sentences each): ## System Components (list main components), ## Data Model (key entities), ## Project Structure (folder layout — the repo root IS the solution root; place .sln at root, project files under ProjectName/ subfolder; NEVER create multiple levels of same-named folders), ## Technology Choices. Keep the entire document under 300 words. Be specific about file paths and component names.
