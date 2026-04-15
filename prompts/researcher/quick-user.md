---
version: "1.0"
description: "Quick-mode research user prompt for concise technology recommendations"
variables:
  - project_description
  - tech_stack
  - topic
---
Project: {{project_description}}
Tech Stack: {{tech_stack}}
Topic: {{topic}}

Write ONE concise paragraph summarizing the key technology recommendations for this project. Be specific about libraries and patterns. Keep it under 150 words.
