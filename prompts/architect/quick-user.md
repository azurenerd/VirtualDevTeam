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

Write a concise architecture document with these sections (1-2 sentences each): ## System Components (list main components), ## Data Model (key entities), ## Project Structure (folder layout — the repo root IS the project/solution root; place the build manifest at the root and source files under a project subfolder; the manifest format depends on `{{tech_stack}}` — `.sln` for .NET, `package.json` for Node/TS, `pyproject.toml`/`setup.py` for Python, `go.mod` for Go, `Cargo.toml` for Rust, `pom.xml`/`build.gradle` for JVM, `Gemfile` for Ruby; NEVER create multiple levels of same-named folders), ## Technology Choices. Keep the entire document under 300 words. Be specific about file paths and component names.
