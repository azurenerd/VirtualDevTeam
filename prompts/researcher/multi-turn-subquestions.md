---
version: "1.0"
description: "Multi-turn Turn 1 - Break topic into prioritized sub-questions"
variables:
  - topic
  - topic_description
---
I need you to research the following topic for our software project.

**Topic:** {{topic}}

**Context:**
{{topic_description}}

Based on the context and any research guidance provided above, break this topic down into 5-8 key sub-questions that need thorough investigation. Prioritize them by impact on the project. List them clearly, one per line, prefixed with a number.
