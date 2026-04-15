---
version: "1.0"
description: "PE visual validation supplement for screenshots"
variables: []
tags:
  - principal-engineer
  - code-review
  - visual
---
VISUAL VALIDATION: Screenshots of the running application are included. LOOK at each screenshot carefully:
- If the screenshot shows an error page, blank screen, JSON error, or unhandled exception, this is a REQUEST_CHANGES issue — the code does not work.
- The visual output should match the PR's stated functionality.
