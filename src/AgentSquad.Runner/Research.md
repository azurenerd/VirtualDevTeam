# Research: Luxury Pool Construction Knowledge Website

> **Project:** Humphrey Luxury Pools — Educational Content Site
> **Date:** 2026-04-06 (Updated)
> **Status:** Research Complete — Ready for Architecture & Implementation
> **Last Verified:** April 6, 2026 — Astro 6.1.3 confirmed as latest stable; Cloudflare acquisition (Jan 2026) verified; all domain content cross-referenced with industry sources

---

## Key Research Sub-Questions (Prioritized by Impact)

1. **What technology stack best supports a content-heavy luxury educational site that shares the design DNA of the existing vanilla HTML HumphreyPools site?** — Determines every downstream technical decision. *(Answered: Astro 6.x — see §2)*
2. **What are all 10+ construction phases for a $250K+ luxury pool project, and what are the specific quality differences (budget vs. luxury) at each phase?** — The core content that drives the site's value proposition. *(Answered: 10 phases + 6 specialty features — see §6)*
3. **How should the progressive disclosure UX work — high-level overview → phase detail → deep-dive expandable sections — and what components are needed?** — Defines the site architecture and user experience. *(Answered: see §3.2)*
4. **What are the North Texas-specific considerations (expansive clay soil, municipal codes, climate) that make this content regionally authoritative?** — Differentiates from generic pool education content. *(Answered: see §1.5)*
5. **What specialty feature content (acrylic windows, fire features, outdoor kitchens, patio covers, turf) needs dedicated deep-dive pages?** — Expands content scope beyond basic pool construction. *(Answered: see §6, Specialty Features)*
6. **What luxury placeholder images and media strategy will convey the premium brand feel before original photography is available?** — Critical for first impressions and stakeholder buy-in. *(Answered: see §8.3)*
7. **How should the site be structured for future integration with the main HumphreyPools site (shared design tokens, subdomain vs. subdirectory, component reuse)?** — Ensures long-term architectural alignment. *(Answered: see §3.1, §7.3)*
8. **What is the optimal content production pipeline (Markdown/MDX authoring, image optimization, SEO metadata) to efficiently produce 30,000–40,000 words of expert content?** — Practical bottleneck for delivery timeline. *(Answered: see §8.4)*

---

## Table of Contents

1. [Domain & Market Research](#1-domain--market-research)
2. [Technology Stack Evaluation](#2-technology-stack-evaluation)
3. [Architecture Patterns & Design](#3-architecture-patterns--design)
4. [Libraries, Frameworks & Dependencies](#4-libraries-frameworks--dependencies)
5. [Security & Infrastructure](#5-security--infrastructure)
6. [Content Architecture: Pool Construction Phases](#6-content-architecture-pool-construction-phases)
7. [Risks, Trade-offs & Open Questions](#7-risks-trade-offs--open-questions)
8. [Implementation Recommendations](#8-implementation-recommendations)

---

## 1. Domain & Market Research

### 1.1 Core Domain Concepts & Terminology

This site targets the **luxury custom pool construction** market ($250K+ projects). Key terminology the content must cover:

| Category | Key Terms |
|----------|-----------|
| **Structural** | Gunite, shotcrete, rebar schedule, steel cage, PSI rating (4,000–7,000), engineered plans, stamped calculations, soil report, cold joints |
| **Finishes** | Waterline tile, coping, pebble finish (PebbleTec/PebbleSheen), quartz aggregate, glass bead, marcite plaster, travertine, porcelain pavers |
| **Water Features** | Infinity/vanishing edge, perimeter overflow, spillover spa, laminar jets, bubblers, deck jets, sheer descents, scuppers, grottos |
| **Fire Features** | Fire bowls, sunken fire pit, fire-and-water bowls, gas vs. ethanol burners, copper fire pots, linear fire troughs |
| **Specialty** | Acrylic pool windows, swim-up bars, tanning ledges (Baja shelves), beach entries, underwater benches |
| **Outdoor Living** | Outdoor kitchen, built-in grill, pergola/louvered roof, patio cover, pavilion, artificial turf, landscape lighting |
| **Equipment** | Variable-speed pumps, salt chlorine generators, automation systems (Pentair/Jandy), LED color lighting, ozone/UV sanitation |
| **Process** | Layout/staking, excavation, plumbing rough-in, steel/rebar, gunite shoot, tile & coping, decking, plaster/finish, startup, pool school |

### 1.2 Target Users

**Primary Audience:** Affluent homeowners ($500K–$5M+ home values) in North Dallas (Frisco, Prosper, McKinney, Celina, Allen, Plano) considering a luxury backyard transformation.

**Key User Workflows:**
1. **Research Phase** — High-level overview of what building a luxury pool entails ("What am I getting into?")
2. **Deep Dive** — Phase-by-phase education with quality vs. cut-corner comparisons ("How do I evaluate builders?")
3. **Feature Exploration** — Understanding specialty elements: acrylic windows, fire features, outdoor kitchens, turf
4. **Trust Building** — Seeing that Humphrey Pools understands every detail at an expert level
5. **Conversion** — Scheduling a consultation after being educated and impressed

### 1.3 Competitive Landscape

| Competitor Type | Examples | Gaps This Site Fills |
|----------------|----------|---------------------|
| Generic pool builder sites | Mission Pools, Premier Pools | Surface-level process pages; no quality comparison detail |
| Pool education blogs | Pool Research, River Pools blog | Informational but no luxury focus; not brand-aligned |
| Luxury pool builders | Platinum Pools, Southern Pool Designs | Beautiful portfolios but shallow on educational content |
| YouTube pool content | Pool Guy, Swimming Pool Steve | Video-first; no structured progressive-disclosure reading experience |

**Differentiator:** No luxury pool builder combines deep educational content (phase-by-phase quality comparisons) with a luxury brand experience. This site fills that exact gap.

### 1.4 Compliance & Standards

- **No regulated content** — This is educational/marketing, not e-commerce or healthcare
- **ADA/WCAG 2.1 AA** — Important for accessibility and SEO
- **Texas pool construction codes** — Referenced in content but not a compliance burden for the website itself
- **Image licensing** — Placeholder images must use royalty-free luxury pool photography (Unsplash, Pexels) replaceable with original photography later

### 1.5 North Texas Regional Considerations

This content targets a specific geography — North Dallas (Celina, Frisco, Prosper, McKinney, Allen, Plano). Regional expertise is a key differentiator:

| Factor | Details | Impact on Content |
|--------|---------|-------------------|
| **Expansive Clay Soil** | 90%+ of DFW sits on expansive clay that swells when wet, contracts when dry — causes ground movement of 2–6 inches seasonally | Every phase from excavation to decking must address this; major content differentiator |
| **Soil Stabilization** | Chemical soil injections (e.g., SWIMSOIL, Earthlok, ESSL, Atlas Soil Stabilization), engineered piers, helical piles driven to stable strata below clay; standard injection depth ~7 feet, deeper for high-swell zones | Quality-tier differentiator: budget builders skip this ($3K–$8K savings that causes $30K+ in damage) |
| **Geotechnical Reports** | Soil borings determine clay composition, plasticity index, bearing capacity | Content should explain why this $500–$1,500 report prevents catastrophic structural failure |
| **Climate** | Hot summers (100°F+), mild winters, occasional hard freezes, severe thunderstorms | Impacts material selection (heat-resistant deck materials, freeze-thaw tile considerations, drainage for heavy rain) |
| **Municipal Variations** | Each city (Frisco, Prosper, Celina, McKinney) has different setback requirements, permit timelines, and inspection schedules | Content opportunity: builder who handles all permitting across municipalities |
| **HOA Considerations** | Many luxury neighborhoods have Architectural Review Committees (ARC) with specific requirements | Adds a pre-construction approval phase not present in generic content |
| **Construction Season** | Year-round possible but optimal March–November; concrete curing affected by extreme heat/cold | Content about timing your project and seasonal quality considerations |

**Why this matters for the site:** Generic pool education sites ignore regional soil and climate. A dedicated "Building in North Texas Clay Soil" callout in the excavation, steel, gunite, and decking phases makes this content uniquely authoritative and locally trusted.

---

## 2. Technology Stack Evaluation

### 2.1 Existing Site Analysis (HumphreyPools GitHub Repo)

The reference repo (`azurenerd/HumphreyPools`) is a **single-page vanilla HTML/CSS/JS** site with:

| Aspect | Details |
|--------|---------|
| **Structure** | Single `index.html` (~23KB), `css/styles.css` (~24KB), `js/main.js` (~5KB) |
| **Design System** | CSS custom properties: `--color-primary: #0a1628`, `--color-accent: #c9a84c` (gold), `--color-offwhite: #f7f5f0` |
| **Typography** | Playfair Display (headings), Raleway (body), Cormorant Garamond (accent) — Google Fonts |
| **Interactivity** | IntersectionObserver for scroll reveals, smooth scrolling, counter animation, mobile menu, video autoplay on visibility |
| **No Dependencies** | Zero npm packages, no build step, no framework |
| **Content Sections** | Hero, About, Video Showcase, Process (4 steps), Services (3 cards), Gallery, Trust/Stats, CTA, Social, Footer |

**Critical Constraint:** The new site must share the same design DNA so it can be merged into or linked from the existing site seamlessly.

### 2.2 Candidate Technology Stacks

#### Option A: Astro 6 (⭐ RECOMMENDED)

| Dimension | Details |
|-----------|---------|
| **Framework** | Astro 6.1.3 (latest stable as of April 2026; 6.0 released March 10, 2026) |
| **Rendering** | Static Site Generation (SSG) — zero client JS by default |
| **Styling** | Vanilla CSS using existing design tokens from HumphreyPools, scoped component styles |
| **Content** | Astro Content Collections (Markdown/MDX files for each phase section) + Live Content Collections (new in 6.0 — can fetch from external CMS at runtime if needed later) |
| **Fonts** | Built-in Fonts API (new in Astro 6) — auto-downloads, caches, self-hosts Google Fonts for performance |
| **Interactivity** | Astro Islands — hydrate only expand/collapse and navigation components |
| **Build Tool** | Vite 7 (built-in, upgraded from Vite 6 in Astro 5) |
| **Runtime** | Node.js 22 (required by Astro 6; Node 18/20 no longer supported) |
| **Schema** | Zod 4 for content validation (upgraded from Zod 3 in Astro 5) |
| **Syntax Highlighting** | Shiki 4 (upgraded from Shiki 1.x in Astro 5) |
| **Output** | Static HTML/CSS/JS — identical output format to existing site |
| **Learning Curve** | Low-moderate (HTML-like `.astro` component syntax) |

**Why Astro 6 wins for this project:**
- **Design compatibility**: Astro outputs plain HTML/CSS/JS — can share stylesheets, fonts, and design tokens with the existing vanilla site 1:1
- **Content-first**: Built for exactly this use case — lots of editorial content with progressive disclosure
- **Islands architecture**: Only the interactive expand/collapse sections get JavaScript; everything else is pure HTML
- **Built-in Fonts API** (new in Astro 6): Automatically downloads, caches, and self-hosts Google Fonts (Playfair Display, Raleway, Cormorant Garamond) — eliminates render-blocking external font requests and improves Core Web Vitals
- **Content Security Policy (CSP) API** (new in Astro 6): Built-in CSP hashing for scripts and styles — security best practice with zero configuration
- **Redesigned Dev Server** (new in Astro 6): Uses Vite's Environment API so dev mirrors production runtime exactly
- **Content Layer with `glob()` loader**: Markdown builds are up to 5x faster with 25–50% less memory usage vs. legacy Content Collections
- **Zero lock-in**: If the team wants to migrate back to vanilla HTML later, Astro's output IS vanilla HTML
- **SEO**: Pre-rendered HTML with perfect Core Web Vitals scores
- **Component reuse**: Shared header, footer, design tokens across both sites without code duplication
- **Server Islands**: Can add dynamic components (e.g., contact form, analytics) to otherwise static pages without full SSR
- **Experimental Rust Compiler**: Astro 6 introduces an experimental Rust-based compiler (replacing the legacy Go compiler), anticipated to significantly improve build performance and scalability in future releases
- **Live Content Collections**: Previously build-time only, content collections can now fetch from external sources at runtime — future-proofs the site for headless CMS integration without refactoring
- **Note on Cloudflare acquisition**: Astro was acquired by Cloudflare in January 2026 but remains MIT-licensed and open source. Cloudflare Pages is now the "golden path" deployment target, but GitHub Pages, Netlify, and Vercel are still fully supported. The full Astro team joined Cloudflare and continues working on Astro full-time

#### Option B: Enhanced Vanilla HTML/CSS/JS

| Dimension | Details |
|-----------|---------|
| **Framework** | None |
| **Rendering** | Static HTML files |
| **Styling** | CSS with shared design tokens |
| **Content** | Hand-coded HTML sections |
| **Interactivity** | Custom JS for expand/collapse |
| **Build Tool** | None |
| **Output** | Static HTML/CSS/JS |
| **Learning Curve** | Lowest |

**Pros:** Zero tooling, identical to existing site, no build step
**Cons:** Content duplication across pages, no component reuse, manual maintenance of repeated elements (header, footer, nav), difficult to scale to 15+ deep-dive sections. Would result in massive HTML files or many nearly-identical HTML files with duplicated chrome.

#### Option C: Next.js 15 (Static Export)

| Dimension | Details |
|-----------|---------|
| **Framework** | Next.js 15.x with App Router |
| **Rendering** | Static Export (`output: 'export'`) |
| **Styling** | CSS Modules or Tailwind |
| **Content** | MDX with `next-mdx-remote` |
| **Interactivity** | React components |
| **Build Tool** | Webpack/Turbopack |
| **Output** | Static HTML/CSS/JS (with React runtime) |
| **Learning Curve** | Moderate-high |

**Pros:** Powerful ecosystem, great DX
**Cons:** Ships React runtime (~40KB+) for a content site that doesn't need it; design system diverges from vanilla CSS approach; overkill for this use case; harder to merge with existing vanilla site

### 2.3 Stack Decision Matrix

| Criteria (Weight) | Astro 6 | Vanilla HTML | Next.js 15 |
|-------------------|---------|-------------|------------|
| Design compatibility with existing site (25%) | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ |
| Content management at scale (20%) | ⭐⭐⭐⭐⭐ | ⭐⭐ | ⭐⭐⭐⭐ |
| Performance / Core Web Vitals (15%) | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| Progressive disclosure UX (15%) | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| Developer productivity (10%) | ⭐⭐⭐⭐⭐ | ⭐⭐ | ⭐⭐⭐⭐ |
| Future extensibility (10%) | ⭐⭐⭐⭐⭐ | ⭐⭐ | ⭐⭐⭐⭐⭐ |
| Team learning curve (5%) | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ |
| **Weighted Score** | **4.85** | **3.40** | **3.90** |

### 2.4 Recommended Stack

> **Primary: Astro 6.1.3** with vanilla CSS (porting existing design tokens), Content Collections for phase content, built-in Fonts API for self-hosted Google Fonts, deployed to **GitHub Pages** (free, matches existing repo hosting). Alternatively **Cloudflare Pages** (free unlimited bandwidth, now Astro's "golden path" after January 2026 acquisition).

---

## 3. Architecture Patterns & Design

### 3.1 Site Architecture Pattern: Static Site with Islands

```
┌─────────────────────────────────────────────┐
│                ASTRO BUILD                   │
│  ┌─────────────────────────────────────────┐ │
│  │  Content Collections (Markdown/MDX)     │ │
│  │  ├── phases/01-design-planning.md       │ │
│  │  ├── phases/02-excavation.md            │ │
│  │  ├── phases/03-steel-rebar.md           │ │
│  │  ├── phases/04-plumbing.md              │ │
│  │  ├── phases/05-gunite-shotcrete.md      │ │
│  │  ├── phases/06-tile-coping.md           │ │
│  │  ├── phases/07-decking-hardscape.md     │ │
│  │  ├── phases/08-equipment.md             │ │
│  │  ├── phases/09-interior-finish.md       │ │
│  │  ├── phases/10-startup-orientation.md   │ │
│  │  ├── features/acrylic-windows.md        │ │
│  │  ├── features/fire-features.md          │ │
│  │  ├── features/outdoor-kitchen.md        │ │
│  │  ├── features/patio-covers.md           │ │
│  │  └── features/turf-landscaping.md       │ │
│  └─────────────────────────────────────────┘ │
│  ┌─────────────────────────────────────────┐ │
│  │  Layouts & Components (.astro)          │ │
│  │  ├── LuxuryLayout.astro (shared chrome) │ │
│  │  ├── PhaseCard.astro                    │ │
│  │  ├── QualityComparison.astro            │ │
│  │  ├── ComparisonTable.astro              │ │
│  │  ├── ExpandableSection.astro            │ │
│  │  ├── PhaseTimeline.astro                │ │
│  │  └── HeroSection.astro                  │ │
│  └─────────────────────────────────────────┘ │
│                    ↓ BUILD ↓                 │
│  ┌─────────────────────────────────────────┐ │
│  │  Static Output (HTML/CSS/JS)            │ │
│  │  ├── index.html (overview + timeline)   │ │
│  │  ├── phases/design-planning/index.html  │ │
│  │  ├── phases/excavation/index.html       │ │
│  │  ├── ... (one page per phase)           │ │
│  │  ├── features/acrylic-windows/index.html│ │
│  │  └── css/styles.css (shared design)     │ │
│  └─────────────────────────────────────────┘ │
└─────────────────────────────────────────────┘
```

### 3.2 Page Structure: Progressive Disclosure Pattern

Each construction phase page follows this template:

```
┌──────────────────────────────────────────┐
│  PHASE HERO (image + title + number)     │
├──────────────────────────────────────────┤
│  EXECUTIVE SUMMARY                       │
│  "What luxury builders do differently"   │
│  (3-4 bullet gold-accented callout box)  │
├──────────────────────────────────────────┤
│  COMPARISON TABLE                        │
│  Standard Builder │ Quality │ Luxury     │
│  ─────────────────┼─────────┼──────────  │
│  Detail row 1     │  ...    │  ...       │
│  Detail row 2     │  ...    │  ...       │
├──────────────────────────────────────────┤
│  DEEP DIVE SECTIONS (expandable)         │
│  ▸ What Happens In This Phase            │
│  ▸ Materials & Specifications            │
│  ▸ Common Pitfalls & Red Flags           │
│  ▸ Questions to Ask Your Builder         │
│  ▸ What We Do Differently                │
├──────────────────────────────────────────┤
│  PHASE GALLERY (3-4 luxury images)       │
├──────────────────────────────────────────┤
│  NAVIGATION (← Previous | Next →)        │
└──────────────────────────────────────────┘
```

### 3.3 Information Architecture

```
HOME (Overview + Interactive Phase Timeline)
├── Phase 1: Design & Planning
├── Phase 2: Excavation & Layout
├── Phase 3: Steel & Rebar
├── Phase 4: Plumbing & Electrical
├── Phase 5: Gunite / Shotcrete
├── Phase 6: Tile, Coping & Masonry
├── Phase 7: Decking & Hardscape
├── Phase 8: Equipment & Automation
├── Phase 9: Interior Finish (Plaster/Pebble/Quartz)
├── Phase 10: Startup, Inspection & Pool School
│
├── SPECIALTY FEATURES
│   ├── Acrylic Pool Windows
│   ├── Fire Features (Bowls, Sunken Pits, Linear Troughs)
│   ├── Spas & Water Features
│   ├── Outdoor Kitchens
│   ├── Patio Covers & Pergolas
│   └── Artificial Turf & Landscaping
│
└── ABOUT / CONTACT (links back to main HumphreyPools site)
```

### 3.4 Data Storage Strategy

**No database required.** All content lives in Markdown files within the Astro project, managed via the **Astro 6 Content Layer** with Zod schemas for type safety. This is a purely static, content-driven site.

- **Phase content**: Markdown with YAML frontmatter (title, phase number, hero image, summary bullets, comparison table data), loaded via `glob()` loader
- **Schema validation**: Zod schemas ensure every phase file has required fields (title, phaseNumber, heroImage, executiveSummary, comparisonRows)
- **Images**: Static assets in `public/images/` (placeholder Unsplash/Pexels images initially), optimized at build time via Sharp
- **Design tokens**: Shared CSS custom properties file imported across all pages

**Example Content Layer config:**
```js
import { defineCollection, z } from 'astro:content';
import { glob } from 'astro/loaders';

const phases = defineCollection({
  loader: glob({ pattern: "**/*.md", base: "./src/content/phases" }),
  schema: z.object({
    title: z.string(),
    phaseNumber: z.number(),
    duration: z.string(),
    heroImage: z.string(),
    executiveSummary: z.string(),
    comparisonRows: z.array(z.object({
      aspect: z.string(),
      budget: z.string(),
      luxury: z.string(),
    })),
  }),
});

const features = defineCollection({
  loader: glob({ pattern: "**/*.md", base: "./src/content/features" }),
  schema: z.object({
    title: z.string(),
    heroImage: z.string(),
    category: z.enum(['water', 'fire', 'outdoor-living', 'landscape']),
  }),
});

export const collections = { phases, features };
```

### 3.5 No API Needed

This is a static content site. No REST, GraphQL, or backend API is required. If a contact form is desired later, it can use a third-party service (Formspree, Netlify Forms) without backend infrastructure.

---

## 4. Libraries, Frameworks & Dependencies

### 4.1 Core Dependencies

| Package | Version | Purpose | License |
|---------|---------|---------|---------|
| `astro` | `^6.1.3` (latest stable) | Framework / SSG | MIT |
| `@astrojs/mdx` | `^4.x` | MDX support for rich content | MIT |
| `sharp` | `^0.34.x` | Image optimization (built-in Astro integration) | Apache-2.0 |

> **Note:** Astro 6 requires **Node.js 22**, uses **Vite 7**, **Zod 4**, and **Shiki 4** internally. The built-in Fonts API replaces the need for manual Google Fonts `<link>` tags. The legacy Content Collections API has been removed — all collections must use the new Content Layer API with `glob()` loader.

### 4.2 Optional Enhancements

| Package | Version | Purpose | License |
|---------|---------|---------|---------|
| `astro-icon` | `^1.x` | SVG icon components | MIT |
| `@astrojs/sitemap` | `^3.x` | Auto-generated sitemap for SEO | MIT |
| `@astrojs/rss` | `^4.x` | RSS feed (if blog content added later) | MIT |

### 4.3 Development Tooling

| Tool | Purpose |
|------|---------|
| `prettier` + `prettier-plugin-astro` | Code formatting |
| `eslint` | JS linting |
| GitHub Actions | CI/CD pipeline for build + deploy to GitHub Pages |

### 4.4 Fonts (External, No Package)

Loaded via Google Fonts CDN (same as existing site):
- **Playfair Display** (headings) — OFL license
- **Raleway** (body) — OFL license  
- **Cormorant Garamond** (accent/italic) — OFL license

### 4.5 No Licensing Concerns

All recommended dependencies are MIT or Apache-2.0 licensed. No GPL or restrictive licenses in the dependency tree.

---

## 5. Security & Infrastructure

### 5.1 Security Profile

This is a **static site with no backend, no database, no user accounts, and no sensitive data**. The security surface is minimal:

| Concern | Mitigation |
|---------|------------|
| XSS | No user input; all content is pre-rendered at build time |
| Content injection | Markdown content is sanitized by Astro's built-in renderer |
| Dependencies | Minimal deps (3 core packages); `npm audit` in CI |
| Image hotlinking | Use optimized local images, not hotlinked external URLs |

### 5.2 Hosting & Deployment

**Recommended: GitHub Pages** (free tier)

| Aspect | Details |
|--------|---------|
| **Provider** | GitHub Pages via GitHub Actions |
| **Cost** | $0/month (included with GitHub free tier) |
| **CDN** | GitHub's global CDN (Fastly-backed) |
| **SSL** | Free, automatic HTTPS |
| **Custom Domain** | Supported (e.g., `guide.humphreyluxurypools.com`) |
| **Build** | GitHub Actions workflow: `npm run build` → deploy `dist/` |
| **Bandwidth** | 100GB/month soft limit (more than sufficient) |

**Alternative options if needs grow:**

| Provider | Free Tier | When to Consider |
|----------|-----------|------------------|
| **Netlify** | 100GB bandwidth, 300 build min/month | If form handling or serverless functions needed |
| **Vercel** | 100GB bandwidth, unlimited deploys | If SSR or edge functions needed later |
| **Cloudflare Pages** | Unlimited bandwidth | If global CDN performance becomes critical |

### 5.3 Infrastructure Cost Estimates

| Scale | Monthly Cost | Notes |
|-------|-------------|-------|
| **Small** (< 10K visitors/month) | **$0** | GitHub Pages free tier handles this easily |
| **Medium** (10K–100K visitors/month) | **$0–$20** | Still free on GitHub Pages; $20 if custom domain + Cloudflare |
| **Large** (100K+ visitors/month) | **$0–$50** | Cloudflare Pages (free unlimited bandwidth) or Netlify Pro |

### 5.4 CI/CD Pipeline

```yaml
# .github/workflows/deploy.yml
name: Deploy to GitHub Pages
on:
  push:
    branches: [main]
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: 22
      - run: npm ci
      - run: npm run build
      - uses: actions/upload-pages-artifact@v3
        with:
          path: dist/
  deploy:
    needs: build
    runs-on: ubuntu-latest
    permissions:
      pages: write
      id-token: write
    environment:
      name: github-pages
    steps:
      - uses: actions/deploy-pages@v4
```

---

## 6. Content Architecture: Pool Construction Phases

This section outlines the **domain content** that each phase page should cover. This is the core value of the site — deep, expert-level education that builds trust and differentiates Humphrey Pools.

### Phase 1: Design & Planning (2–6 weeks)

**Executive Summary:** The most important phase. Luxury builders invest weeks in detailed 3D design and engineering; budget builders rush to get a deposit and start digging.

| Aspect | Budget Builder | Quality Builder | Luxury Builder (Humphrey) |
|--------|---------------|-----------------|--------------------------|
| **Design Tool** | Hand sketch or basic 2D | 3D rendering software | Photorealistic 3D walkthrough + VR option |
| **Engineering** | Generic template plans | Engineered plans (basic) | Stamped engineer plans with soil report |
| **Site Assessment** | Visual only | Basic survey | Full geotechnical soil report + utility locate + drainage analysis |
| **Design Iterations** | 1 revision | 2–3 revisions | Unlimited until perfect |
| **Permit Handling** | Homeowner responsibility | Builder assists | Builder handles 100% |
| **Timeline** | 1 week | 2–3 weeks | 3–6 weeks (thoroughness over speed) |

**Deep dive topics:**
- Why a soil/geotech report matters ($500 that saves $50,000)
- How 3D design prevents costly change orders
- Red flags: builders who won't show engineering drawings
- What stamped engineer plans actually include and why they matter
- Permit process in North Dallas municipalities

### Phase 2: Excavation & Layout (1–2 weeks)

**Executive Summary:** Precision layout determines everything downstream. Off by 2 inches here = problems for months. In North Texas, expansive clay soil makes this phase especially critical.

| Aspect | Budget Builder | Luxury Builder |
|--------|---------------|----------------|
| **Layout Method** | Spray paint from sketch | GPS/laser staking from CAD plans |
| **Excavation Equipment** | Standard backhoe | Precision excavator with experienced operator |
| **Depth Accuracy** | ±6 inches | ±1 inch |
| **Soil Hauling** | Dumped on-site | Hauled off or precision-graded for landscape |
| **Utility Protection** | Assumed locations | Professional utility locate (811 + private locate) |
| **Benching/Shoring** | Minimal | Full OSHA-compliant benching |
| **Soil Stabilization** | Skipped entirely | Chemical soil injection (SWIMSOIL or equivalent) for expansive clay |
| **Geotechnical Report** | Not performed | Full soil boring analysis with plasticity index and bearing capacity |

**Deep dive topics:**
- What happens when excavation reveals rock, water table, or unstable soil
- **North Texas Clay Alert**: Why expansive clay causes 2–6 inches of seasonal ground movement and how soil stabilization ($3K–$8K) prevents $30K+ in future damage
- How elevation changes affect pool/spa integration
- Common dig mistakes and their downstream costs
- Why luxury pools need a wider dig (for thick walls, equipment access)
- Geotechnical reports: what soil borings reveal and why the $500–$1,500 investment is non-negotiable in DFW clay

### Phase 3: Steel & Rebar (1–2 weeks)

**Executive Summary:** The skeleton of your pool. This is where the most corners are cut because it's hidden under concrete. A quality rebar job costs $3,000–$5,000 more but prevents $30,000+ in structural failure.

| Aspect | Budget Builder | Luxury Builder |
|--------|---------------|----------------|
| **Rebar Gauge** | #3 (3/8") minimum | #4 (1/2") standard, #5 for walls |
| **Grid Spacing** | 12" on-center | 6"–8" on-center |
| **Overlap/Lap** | Minimal or missing | 40+ bar diameters per ACI standards |
| **Steel Placement** | Center of shell | Positioned per engineer's spec (tension side) |
| **Dowels at Steps/Benches** | Skipped | Full doweling at every transition |
| **Spa Connection** | Single tie | Continuous reinforcement with expansion joints |
| **Inspection** | Builder self-inspects | Independent structural inspection + city inspection |

**Deep dive topics:**
- Why rebar spacing matters (crack prevention, structural load distribution)
- The difference between #3 and #4 rebar in real-world durability
- How to read a steel inspection report
- What "cold joints" are and why they're dangerous
- Red flag: builder who won't let you see the rebar before gunite

### Phase 4: Plumbing & Electrical (1 week)

**Executive Summary:** Undersized plumbing is the #1 cause of poor water circulation, dead spots, and algae problems. Luxury pools need 2–3x the plumbing of a standard pool.

| Aspect | Budget Builder | Luxury Builder |
|--------|---------------|----------------|
| **Pipe Size** | 1.5" PVC everywhere | 2"–3" mains, sized per hydraulic calculations |
| **Return Lines** | 2–3 returns | 6–10+ returns for uniform circulation |
| **Suction Lines** | Single main drain | Dual main drains + skimmer(s) per VGB Act |
| **Plumbing Material** | Schedule 40 PVC | Schedule 40 minimum, flex PVC at equipment |
| **Electrical** | Basic code compliance | Dedicated subpanel, surge protection, smart automation wiring |
| **Gas Lines** | Basic heater line | Sized for heater + spa + fire features + outdoor kitchen |
| **Bonding/Grounding** | Code minimum | Full equipotential bonding grid per NEC 680 |

**Deep dive topics:**
- Hydraulic design: flow rates, TDH (total dynamic head), and why they matter
- Why the VGB Act (Virginia Graeme Baker) matters for drain safety
- Automation pre-wiring: planning for future upgrades
- Common plumbing mistakes that cause air locks and poor circulation

### Phase 5: Gunite / Shotcrete Application (1–2 weeks + 7–14 day cure)

**Executive Summary:** The concrete shell IS your pool. Gunite vs. shotcrete both work when applied correctly, but application skill and proper curing are non-negotiable.

| Aspect | Budget Builder | Luxury Builder |
|--------|---------------|----------------|
| **Method** | Shotcrete (pre-mixed, faster) | Gunite (dry-mix, on-site water control) for custom shapes |
| **Shell Thickness** | 6" minimum (may vary) | 8"–12" uniform, 12"–18" at raised walls/windows |
| **PSI Target** | 3,500+ | 5,000–7,000 PSI |
| **Nozzleman Experience** | Varies | ACI-certified nozzleman |
| **Curing Period** | 3–5 days | 7–14 days with active water curing |
| **Rebound Management** | Left in place | Fully cleaned out before sets |
| **Overspray Cleanup** | Minimal | Full cleanup of surrounding areas |

**Deep dive topics:**
- Gunite vs. shotcrete: when each is appropriate
- What happens during the curing process and why shortcuts crack pools
- How to verify shell thickness (core samples)
- The nozzleman's skill: why this single person determines your pool's lifespan
- Rebound material: what it is and why leaving it in is structural fraud

### Phase 6: Tile, Coping & Masonry (2–3 weeks)

**Executive Summary:** This is where your pool's character is defined. Tile and coping are the most visible elements and the first to show if corners were cut.

| Aspect | Budget Builder | Luxury Builder |
|--------|---------------|----------------|
| **Waterline Tile** | Basic 6×6 ceramic | Custom glass mosaic, iridescent, or hand-painted |
| **Tile Depth** | Single row at waterline | Full waterline band (6–12" deep) + accent tiles |
| **Coping Material** | Poured concrete bull-nose | Natural travertine, limestone, or custom-cut stone |
| **Coping Attachment** | Mortar only | Mortar + mechanical anchoring |
| **Stone Sourcing** | Bulk import (inconsistent) | Hand-selected, grade-A, consistent color |
| **Grout** | Standard sanded grout | Epoxy grout (waterproof, stain-proof, lasts 25+ years) |

**Deep dive topics:**
- Glass tile vs. ceramic vs. porcelain vs. natural stone: durability and aesthetics
- Why travertine coping stays cool (thermal conductivity)
- Grout matters: standard vs. epoxy and the long-term cost difference
- Color coordination: how tile and coping interact with plaster color and water appearance
- Installation precision: lippage tolerance and why millimeters matter

### Phase 7: Decking & Hardscape (2–3 weeks)

**Executive Summary:** The deck connects pool to home and defines the outdoor living space. It's also the largest surface area and highest-traffic zone.

| Aspect | Budget Builder | Luxury Builder |
|--------|---------------|----------------|
| **Material** | Stamped/stained concrete | Travertine pavers, porcelain tile, natural stone |
| **Base Preparation** | Minimal compaction | Engineered base: 4–6" compacted aggregate + sand leveling |
| **Drainage** | Surface grading only | Trench drains, channel drains, French drains as needed |
| **Expansion Joints** | Code minimum | Calculated per thermal expansion for material type |
| **Integration** | Pool deck only | Unified with outdoor kitchen, fire pit, walkways, turf zones |
| **Edge Treatment** | Square cut | Bull-nose, tumbled, or custom profile matching coping |

### Phase 8: Equipment & Automation (1–2 weeks)

**Executive Summary:** Equipment determines ongoing costs, noise, and convenience. A luxury equipment pad costs $5,000–$15,000 more but saves $2,000+/year in energy and chemicals.

| Aspect | Budget Builder | Luxury Builder |
|--------|---------------|----------------|
| **Pump** | Single-speed (high energy cost) | Variable-speed (Pentair IntelliFlo, 80% energy savings) |
| **Sanitation** | Basic liquid chlorine | Salt chlorine generator + UV/ozone secondary |
| **Heating** | Gas heater only | Gas + heat pump hybrid (efficient for year-round use) |
| **Automation** | None or basic timer | Full automation (Pentair IntelliCenter / Jandy iAqualink) with app control |
| **Lighting** | Single white light | Color LED system (Pentair IntelliBrite) with zones |
| **Equipment Pad** | Exposed on concrete | Screened/landscaped enclosure with sound dampening |
| **Equipment Access** | Minimal clearance | Designed for service access with unions at all connections |

### Phase 9: Interior Finish — Plaster, Pebble & Quartz (1 week)

**Executive Summary:** The interior finish determines the color, feel, and lifespan of your pool surface. This is the last thing applied and the most visible.

| Finish Type | Lifespan | Feel | Luxury Look | Cost Range |
|-------------|----------|------|-------------|-----------|
| **White Plaster (Marcite)** | 7–10 years | Very smooth | Classic but basic | $ |
| **Colored Plaster** | 7–12 years | Smooth | Better depth | $$ |
| **Quartz Aggregate** | 12–18 years | Smooth-textured | Shimmering, vibrant | $$–$$$ |
| **Pebble (PebbleTec)** | 15–25+ years | Textured | Natural, exotic | $$$–$$$$ |
| **Glass Bead (PebbleSheen)** | 15–20+ years | Smooth-pebble | Luminous, refined | $$$$ |

**Deep dive topics:**
- How plaster color changes water appearance (white = light blue, dark gray = lagoon)
- Application day: why weather and crew timing are critical
- Startup chemistry: the 30-day process that determines finish longevity
- Why cheap plaster fails: improper calcium chloride, rushed application, bad water chemistry
- Quartz vs. pebble: the real-world comfort-under-foot comparison

### Phase 10: Startup, Inspection & Pool School (1–2 days)

**Executive Summary:** The first 30 days of a new pool are the most critical for surface longevity. Luxury builders provide hands-on startup service; budget builders hand you a manual.

| Aspect | Budget Builder | Luxury Builder |
|--------|---------------|----------------|
| **Startup Service** | Homeowner responsibility | Builder manages 30-day startup chemistry program |
| **Water Balancing** | Basic test + chemicals | LSI (Langelier Saturation Index) balanced bi-weekly |
| **Pool School** | 15-minute walkthrough | 1–2 hour hands-on session with automation demo |
| **Warranty** | 1 year structural | 5+ year structural, 2+ year equipment, finish warranty |
| **Post-Build Support** | "Call if there's a problem" | Scheduled 30/60/90-day check-ins |

---

### Specialty Feature: Acrylic Pool Windows

**Key content points:**
- Custom manufactured panels (lead time: 6–12 weeks) — panels are engineered per pool's water depth, size, and shape
- Require 15–18" thick reinforced walls (vs. standard 10") with concrete U-channel larger than the panel
- Panel thickness ranges from 1.5" to 8"+ depending on window size and water depth (2" typical for residential)
- U-channel construction with waterproof slurry and UV-resistant, non-corrosive sealants
- 92% light transmittance; scratch-repairable without draining (modern coatings help)
- Frame: marine-grade stainless steel recommended for all installations
- Cost: $15,000–$30,000 for standard residential panels (8'×4' range); $50,000–$150,000+ for panoramic, curved, or infinity-edge windows
- Requires specialist installation team (not standard pool crew) — panels delivered crated, positioned with cranes
- Seal inspection/replacement needed every 5 years; gentle cleaning required to prevent scratches
- Top manufacturers (Reynolds Polymer, Lucite) offer 20–30 year panel warranties
- **Luxury differentiator**: Budget builders won't even offer this; it requires structural engineering from day one

| Quality Tier | Standard Builder | Luxury Builder |
|-------------|-----------------|----------------|
| **Panel Sourcing** | N/A (not offered) | Custom-engineered by specialty fabricator |
| **Wall Thickness** | N/A | 15–18" reinforced concrete with U-channel |
| **Sealant** | N/A | UV-resistant, certified waterproof sealant system |
| **Installation Team** | N/A | Dedicated acrylic glazing specialists |
| **Warranty** | N/A | 20–30 year panel + installation warranty |

### Specialty Feature: Fire Features

**Types to cover:**
- **Fire bowls** — Copper or concrete, mounted on pedestals or pool wall; dramatic when lit with water features
- **Sunken fire pits** — Lowered seating area with built-in benches, can include water "moat" effect; creates intimate gathering zone
- **Linear fire troughs** — Modern, clean lines; gas-fed; ideal for contemporary pool designs
- **Fire-and-water bowls** — Dramatic combination, requires dual plumbing (gas + water supply)

**Key quality differences:**
- Gas line sizing (BTU calculations for each burner — undersized = weak flame, oversized = safety risk)
- Wind protection considerations (glass wind guards, recessed placement)
- Electronic vs. manual ignition (electronic with app control for luxury)
- Material quality: cast concrete vs. copper vs. stainless steel (copper develops patina, stainless resists corrosion)
- **Safety**: Non-slip materials for steps/seating around wet-to-dry transition zones
- **Integration**: Sunken fire pit placement relative to pool for optimal sightlines and traffic flow
- **Drainage**: Sunken areas require dedicated drainage to prevent water accumulation

| Quality Tier | Budget Builder | Luxury Builder |
|-------------|---------------|----------------|
| **Fire Bowl Material** | Pre-cast concrete | Hand-poured concrete, hammered copper, or stainless steel |
| **Gas Line** | Basic, minimum sizing | Engineered BTU calculations per burner + manifold |
| **Ignition** | Match-lit or manual | Electronic with smart home/app integration |
| **Wind Protection** | None | Tempered glass wind guards or recessed design |
| **Seating (Sunken Pit)** | Movable furniture | Built-in stone/concrete benches with drainage |
| **Electrical** | Basic outlet nearby | Integrated LED accent lighting + automated control |

### Specialty Feature: Outdoor Kitchens

**Key content points:**
- Stainless steel framing + marine-grade polymer cabinetry (resists moisture, UV, insects)
- Countertops: granite, quartzite, or soapstone (heat + weather resistant); avoid marble (etches/stains)
- Appliance integration: grill, side burners, refrigerator, wine cooler, pizza oven, ice maker
- Plumbing: sink with hot/cold water, gas lines, proper drainage with grease trap
- Electrical: GFCI outlets, task lighting, TV/audio pre-wire, dedicated circuits
- **Louvered pergola integration**: Position kitchen under cover for weather protection and year-round usability
- **Layout principle**: Design for natural traffic flow between cooking, dining, pool, and lounging zones
- **Proximity to house**: Closer = easier utility access, but consider views and spatial balance

| Quality Tier | Budget Builder | Luxury Builder |
|-------------|---------------|----------------|
| **Cabinetry** | Wood or basic metal (rots/rusts) | Marine-grade polymer or 304 stainless steel |
| **Countertop** | Tile or basic granite | Quartzite, soapstone, or premium granite (3cm thick) |
| **Grill** | Basic built-in | Premium brand (Lynx, Alfresco, Twin Eagles) with rotisserie |
| **Refrigeration** | Dorm-size fridge | Full-size outdoor-rated fridge + wine cooler + ice maker |
| **Sink** | Basic bar sink | Full prep sink with hot water, soap dispenser, disposal |
| **Electrical** | Single outlet | Dedicated subpanel, TV pre-wire, surround sound, smart controls |
| **Cover** | None or basic umbrella | Integrated louvered pergola with lighting, fans, heaters |

### Specialty Feature: Patio Covers & Pergolas

**Types to cover:**
- **Aluminum louvered roofs** (e.g., Azenco, StruXure) — Motorized, watertight when closed, adjustable slats for sun/shade control; integrated gutters, lighting, fans, and heating available; 15–25 year lifespan
- **Cedar/hardwood pergolas** — Classic aesthetic, requires periodic sealing (every 2–3 years); natural warmth
- **Steel frame with fabric** — Modern, lightweight; fabric replacement every 5–8 years
- **Full pavilion/roof structure** — Complete weather protection; requires engineered footings and may need building permit

| Quality Tier | Budget Builder | Luxury Builder |
|-------------|---------------|----------------|
| **Material** | Wood pergola (requires maintenance) | Aluminum louvered with motorized controls |
| **Weather Protection** | Partial shade only | Watertight when closed, built-in gutters |
| **Integration** | Standalone structure | Integrated lighting, ceiling fans, radiant heaters |
| **Automation** | Manual | App/remote controlled, rain sensor auto-close |
| **Warranty** | 1–3 years | 10–25 year structural warranty |

### Specialty Feature: Artificial Turf

**Key content points:**
- Base prep: 3–4" excavation, compacted crushed stone, weed barrier
- Quality: multi-tone nylon/polyethylene, 1.5"+ pile height, W-shaped blade for realism
- Infill: antimicrobial sand or rubber granules (silica sand for luxury; rubber for play areas)
- Edging: steel or composite for clean borders; aluminum edging lasts longest
- Heat consideration: turf near pool deck can get hot (140°F+ in Texas sun); specify cool-turf technology or TiO₂ coated fibers
- Drainage: minimum 30 inches/hour drainage rate; critical near pool to handle splash-out and rain
- Pet-friendly options: antimicrobial infill with enhanced drainage for households with pets

| Quality Tier | Budget Builder | Luxury Builder |
|-------------|---------------|----------------|
| **Blade Material** | Single-tone polypropylene (fades in 3–5 years) | Multi-tone nylon/polyethylene blend (10–15 year UV warranty) |
| **Pile Height** | 1" or less (looks flat/artificial) | 1.5–2.25" (realistic, soft underfoot) |
| **Infill** | Basic silica sand only | Antimicrobial coated sand + cool-turf technology |
| **Base Prep** | Minimal compaction | Full 4" compacted aggregate base with engineered drainage |
| **Edging** | Plastic landscape edging | Aluminum or composite with clean stone/paver border |
| **Seaming** | Visible seams | Invisible heat-seamed joints |

### Specialty Feature: Spa & Hot Tub Integration

**Key content points — why the spa deserves its own deep dive:**
- Spa construction is often the most complex element: separate heating, jets, controls, and often raised or spillover design
- Spillover spa (most luxury): spa water cascades into pool creating visual and auditory drama
- Structural: spa shell requires independent reinforcement with expansion joints at pool-spa connection
- Heating: dedicated heater (or heat pump) for rapid heat-up; luxury spas reach 104°F in 20–30 minutes
- Jets: hydrotherapy jet placement is both functional and aesthetic; luxury uses 8–16 jets with variable speed
- Controls: independent spa controls (heat, jets, lighting) separate from pool automation
- Seating: ergonomic benches at varying depths (18", 24", 36") with foot wells and armrests

| Quality Tier | Budget Builder | Luxury Builder |
|-------------|---------------|----------------|
| **Design** | Basic attached rectangle | Custom-shaped, raised spillover with stone veneer |
| **Shell** | Shared wall with pool (structural risk) | Independent shell with engineered expansion joints |
| **Jets** | 4–6 basic jets | 8–16 hydrotherapy jets with variable speed blower |
| **Heating** | Shared heater with pool (slow) | Dedicated spa heater for rapid heat-up |
| **Controls** | Same as pool | Independent spa panel + app control |
| **Interior** | Same finish as pool | Upgraded finish (full tile or premium pebble) |
| **Lighting** | Single light | Multi-zone LED with color scenes |

---

## 7. Risks, Trade-offs & Open Questions

### 7.1 Technical Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Content volume overwhelms development timeline | High | Medium | Phase content development: build framework first, populate content incrementally |
| Placeholder images don't convey luxury feel | Medium | High | Use curated Unsplash luxury pool photography; establish image criteria |
| Design drift from existing HumphreyPools site | Medium | High | Extract exact CSS custom properties and fonts into shared design token file |
| Astro version breaking changes | Low | Medium | Pin exact version; Astro has stable upgrade path |

### 7.2 Content Risks

| Risk | Mitigation |
|------|------------|
| Content feels generic / not specific to Humphrey | Include "What We Do Differently" callouts with Humphrey-specific details |
| Information overload for casual visitors | Progressive disclosure: summary first, expand for details |
| Comparison tables feel confrontational to other builders | Frame as "industry tiers" not "us vs. them" — educational tone |

### 7.3 Open Questions for Stakeholders

1. **Subdomain vs. subdirectory?** — Should this live at `guide.humphreyluxurypools.com` or `humphreyluxurypools.com/guide`?
2. **Contact form destination** — Does this site need its own contact form or link back to the main site?
3. **Photography timeline** — When will original project photos be available to replace placeholders?
4. **Content review process** — Who reviews the technical construction content for accuracy?
5. **Brand guidelines** — Is the existing site's design system (colors, fonts) the final brand standard?
6. **Analytics** — Should Google Analytics or a privacy-first alternative (Plausible/Fathom) be integrated?

### 7.4 Decisions to Make Upfront

- ✅ Technology stack (Astro 6) — Recommended above
- ✅ Hosting provider (GitHub Pages) — Recommended above
- ✅ Design system (inherit from existing site) — Recommended above
- ❓ Subdomain vs. path structure
- ❓ Analytics platform choice
- ❓ Content review/approval workflow

### 7.5 Decisions to Defer

- CMS integration (can add headless CMS later without refactoring)
- Blog/article section (Astro Content Collections make this trivial to add)
- Lead capture / CRM integration
- Multi-language support

---

## 8. Implementation Recommendations

### 8.1 Phased Implementation

#### Phase 1: Foundation (Week 1–2)
- Initialize Astro project with shared design tokens from HumphreyPools
- Build layout components (header, footer, nav, shared chrome)
- Create `PhaseCard`, `ComparisonTable`, `ExpandableSection` components
- Build overview/home page with interactive phase timeline
- Deploy to GitHub Pages with CI/CD

**Deliverable:** Working site with shared design, overview page, and infrastructure

#### Phase 2: Core Content (Week 3–5)
- Write and build Phase 1–5 deep-dive pages (Design through Gunite)
- Populate comparison tables with detailed quality tiers
- Add expandable deep-dive sections
- Curate and optimize placeholder luxury pool images
- Phase navigation (previous/next)

**Deliverable:** First half of construction phases with full progressive disclosure UX

#### Phase 3: Complete Content (Week 6–8)
- Write and build Phase 6–10 deep-dive pages (Tile through Startup)
- Build specialty feature pages (Acrylic Windows, Fire Features, Outdoor Kitchen, etc.)
- Cross-linking between related phases and features
- Mobile responsiveness polish

**Deliverable:** Full content site with all phases and features

#### Phase 4: Polish & Launch (Week 9–10)
- SEO optimization (meta tags, Open Graph, structured data)
- Performance audit (Lighthouse 95+ on all metrics)
- Accessibility audit (WCAG 2.1 AA)
- Analytics integration
- Final image swap with original photography (if available)
- Link integration with main HumphreyPools site

**Deliverable:** Production-ready site

### 8.2 Quick Wins (First Sprint)

1. **Scaffold Astro project** with existing design tokens — proves design compatibility immediately
2. **Build the overview/timeline page** — most impactful visual; demonstrates the progressive disclosure concept
3. **Create one complete phase page** (Phase 3: Steel & Rebar is ideal — most dramatic quality differences) — serves as the template for all others
4. **Deploy to GitHub Pages** — live URL for stakeholder review from day 1

### 8.3 Prototyping Recommendations

Before committing to full content production:

1. **Prototype the expand/collapse UX** — Test with 2–3 users: Do they discover the deep-dive content? Is the progressive disclosure intuitive?
2. **Prototype the comparison table design** — Ensure it reads well on mobile (tables are notoriously difficult on small screens; consider card-based layout on mobile)
3. **Validate image sourcing** — Confirm that Unsplash/Pexels luxury pool imagery meets the quality bar before investing in full content production

### 8.4 Content Production Approach

Each phase page requires:
- **Executive summary** (50–100 words)
- **Comparison table** (6–10 rows × 2–3 columns)
- **4–5 expandable deep-dive sections** (200–400 words each)
- **3–4 curated images**
- **"Questions to Ask Your Builder"** sidebar

**Total estimated content:** ~30,000–40,000 words across all phases and features. This is a significant content investment that should be planned alongside development, not after.

---

## Appendix: Design Token Reference (from existing site)

```css
/* Colors */
--color-primary: #0a1628;        /* Deep navy */
--color-primary-light: #142440;  /* Lighter navy */
--color-accent: #c9a84c;         /* Gold */
--color-accent-light: #e0c97f;   /* Light gold */
--color-white: #ffffff;
--color-offwhite: #f7f5f0;       /* Warm white background */
--color-cream: #ede8dd;           /* Cream */
--color-text: #2c2c2c;           /* Body text */
--color-text-light: #6b6b6b;     /* Secondary text */
--color-overlay: rgba(10, 22, 40, 0.65);

/* Typography */
--font-heading: 'Playfair Display', Georgia, serif;
--font-body: 'Raleway', 'Segoe UI', sans-serif;
--font-accent: 'Cormorant Garamond', Georgia, serif;

/* Layout */
--container-max: 1280px;
--section-padding: 100px 0;
--transition: 0.3s ease;
```

**Key design patterns from existing site:**
- `.section-overline` — Small caps gold text above section titles
- `.reveal` — IntersectionObserver-based scroll animations
- `.btn-primary` — Gold background with dark text
- `.btn-outline` — Transparent with gold border
- Dark navy sections alternate with warm white/cream sections
- Service cards use image backgrounds with gradient overlays

---

## Summary of Key Recommendations

| Decision | Recommendation | Confidence |
|----------|---------------|------------|
| **Framework** | Astro 6.1.3 | High ✅ |
| **Styling** | Vanilla CSS with existing design tokens | High ✅ |
| **Content Format** | Markdown/MDX Content Collections | High ✅ |
| **Hosting** | GitHub Pages (free) | High ✅ |
| **CI/CD** | GitHub Actions | High ✅ |
| **UX Pattern** | Progressive disclosure (overview → phase pages → expandable sections) | High ✅ |
| **Image Strategy** | Curated Unsplash/Pexels placeholders, replaced with originals later | Medium ✅ |
| **CMS** | Defer — add headless CMS only if content editors need it | Medium ✅ |
| **Analytics** | Defer decision to stakeholder input | Low ❓ |
