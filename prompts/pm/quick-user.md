---
version: "1.0"
description: "User prompt for quick PMSpec generation"
variables:
  - project_description
  - tech_stack
tags:
  - pm
  - quick
---
Project: {{project_description}}
Tech Stack: {{tech_stack}}

Write a concise PMSpec with these sections (1-2 sentences each): Executive Summary, Business Goals, User Stories (3-5 bullet points with acceptance criteria), Scope, Non-Functional Requirements. Keep the entire document under 300 words.
