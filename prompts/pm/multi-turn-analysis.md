---
version: "1.0"
description: "Turn 1: Analyze project and identify requirements"
variables:
  - project_name
  - project_description
  - research_doc
tags:
  - pm
  - spec
  - multi-turn
---
I need you to create a PM Specification for our project.

**Project Name:** {{project_name}}

**Project Description:**
{{project_description}}

## Research Findings
{{research_doc}}

Based on this information, identify:
1. The core business goals and objectives
2. Key user stories with acceptance criteria
3. What's in scope and what's explicitly out of scope
4. Non-functional requirements (performance, security, scalability, reliability)
5. Success metrics — how we know the project is done
6. Key constraints and assumptions

Be specific and actionable. Each user story should have clear acceptance criteria.
