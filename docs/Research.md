# Research: Luxury Pool Construction Educational Website

> **Project**: Humphrey Luxury Pools — Construction Process Educational Site
> **Date**: April 2026
> **Status**: Research Complete — Ready for Architecture & Implementation
> **Target**: $250K+ luxury pool, spa, and outdoor living construction education

---

## Table of Contents

1. [Key Sub-Questions Prioritized by Impact](#1-key-sub-questions-prioritized-by-impact)
2. [Domain & Market Research](#2-domain--market-research)
3. [Technology Stack Evaluation](#3-technology-stack-evaluation)
4. [Architecture Patterns & Design](#4-architecture-patterns--design)
5. [Libraries, Frameworks & Dependencies](#5-libraries-frameworks--dependencies)
6. [Security & Infrastructure](#6-security--infrastructure)
7. [Risks, Trade-offs & Open Questions](#7-risks-trade-offs--open-questions)
8. [Implementation Recommendations](#8-implementation-recommendations)
9. [Appendix: Domain Content Inventory](#appendix-a-domain-content-inventory)

---

## 1. Key Sub-Questions Prioritized by Impact

1. **What technology stack best serves a content-heavy, visually rich luxury site that must share design DNA with the existing HumphreyPools static HTML site?** — Determines entire project foundation.
2. **What is the complete set of construction phases and specialty features that need to be documented, and how deep does each section go?** — Defines content scope and information architecture.
3. **What UX pattern best enables progressive disclosure (high-level overview → deep detail) for 12+ construction phases?** — Defines the core user experience and navigation model.
4. **How should luxury-vs-budget quality differences be presented at each phase to maximize educational impact?** — Core value proposition of the site; needs consistent, scannable design treatment.
5. **What animation, imagery, and interaction patterns create a "luxury feel" while maintaining performance?** — Visual identity must match the $250K+ market positioning.
6. **How can the site be built so it integrates naturally with the existing HumphreyPools.com site in the future?** — Shared design tokens, fonts, color palette, and component patterns.
7. **What is the optimal content management approach for a non-developer to update construction details, images, and tables?** — Determines CMS strategy (or lack thereof).
8. **What hosting and deployment strategy minimizes cost while ensuring fast global delivery of image/video-heavy content?** — CDN, image optimization, and hosting platform selection.

---

## 2. Domain & Market Research

### 2.1 Core Domain Concepts & Terminology

The luxury pool construction process involves **12 distinct phases** from pre-construction through post-construction, plus **6 specialty feature categories** unique to $250K+ projects:

#### Construction Phases

| # | Phase | Duration | Critical Factor |
|---|-------|----------|-----------------|
| 01 | Site Assessment & Engineering | 1–4 weeks | Soil testing, topography, utility mapping |
| 02 | Design & 3D Visualization | 2–6 weeks | CAD → 3D photorealistic renderings |
| 03 | Permitting & HOA Approval | 2–8 weeks | Building, electrical, plumbing, gas permits |
| 04 | Layout & Excavation | 1–3 days | GPS-guided, soil analysis, depth verification |
| 05 | Plumbing Rough-In | 1–2 days | Schedule 40 PVC, pressure testing |
| 06 | Steel & Rebar Framework | 1–2 days | #4–#5 bar, 6–8" spacing, double mat |
| 07 | Electrical Rough-In | 1–2 days | NEC 680 compliance, equipotential bonding |
| 08 | Gunite/Shotcrete Shell | 1 day + 7 day cure | 8–12" walls, ACI-certified nozzleman |
| 09 | Tile, Coping & Waterline | 3–10 days | Glass mosaic, imported stone, flexible grout |
| 10 | Interior Finish | 1–2 days | PebbleTec, glass bead (15–25 yr lifespan) |
| 11 | Decking & Hardscape | 3–14 days | Travertine pavers, proper base prep |
| 12 | Equipment, Fill & Startup | 3–7 days | Automation, 28-day startup protocol |

#### Specialty Features ($250K+ Projects)

| Feature | Key Consideration | Cost Range |
|---------|------------------|------------|
| **Sunken Fire Pits** | Drainage is critical; gas preferred; 10ft+ from pool | $15K–$60K |
| **Fire Bowls / Fire Features** | Gas line sizing for BTU demand; copper vs GFRC | $5K–$30K each |
| **Acrylic Pool Windows** | Structural engineering, 2"–12" thickness, specialist install | $15K–$200K+ |
| **Outdoor Kitchens** | Marine-grade appliances; granite/quartzite countertops | $30K–$150K+ |
| **Patio Covers / Pergolas** | Wind/snow load engineering; motorized louvers | $20K–$80K+ |
| **Artificial Turf** | 4–6" multi-layer base; chlorine-resistant fiber | $8K–$25K |

### 2.2 Target Users & Key Workflows

**Primary persona**: Affluent homeowner (household income $300K+) in North Dallas/DFW area, researching a $250K+ backyard transformation. They want to:

1. **Understand the process** before committing — reduce fear of the unknown
2. **Know what quality looks like** — be able to evaluate builder proposals
3. **Identify red flags** — protect their investment from corner-cutting
4. **Feel confident** in Humphrey Luxury Pools as the right builder

**User journey**:
- Land on overview → scan all phases → dive deep into 2–3 phases of interest → compare luxury vs budget → contact for consultation

### 2.3 Competitive Landscape

| Competitor | Approach | Strengths | Weaknesses |
|-----------|----------|-----------|------------|
| Saturn Pools | Gallery-forward with process overview | Beautiful photography | Shallow process detail |
| California Pools | FAQ-style education per location | SEO-optimized, local | Generic, not luxury-focused |
| Ferrari Pools | Step-by-step construction guide blog | Good process detail | Dry, no luxury differentiation |
| Blackburn Group Pools | Gunite process walkthrough | Technical accuracy | No comparison tables, no specialty features |
| SwiftlyUI pool sites | Lifestyle branding + lead gen | Premium feel, conversion-focused | Template-based, no deep education |

**Opportunity**: No competitor combines luxury visual design + deep phase-by-phase education + quality comparison tables + specialty feature coverage. This is a clear whitespace.

### 2.4 Industry Standards & Compliance

- **NEC Article 680**: Governs all pool electrical work (bonding, grounding, GFCI)
- **Virginia Graeme Baker Act (VGB)**: Anti-entrapment drain safety requirements
- **ICC/IBC Building Codes**: Structural, setback, fencing requirements
- **ASTM A615**: Rebar grade specification
- **ACI/ASA Certification**: Gunite nozzleman qualifications
- **Local codes**: Texas-specific pool fencing, permitting, HOA requirements

---

## 3. Technology Stack Evaluation

### 3.1 Existing Site Analysis

The current HumphreyPools site (GitHub: `azurenerd/HumphreyPools`) is:

- **Pure static**: Single `index.html` + `css/styles.css` + `js/main.js`
- **No build tools**: No bundler, no framework, no package.json
- **Design tokens**:
  - Primary: `#0a1628` (deep navy), `#142440` (navy light)
  - Accent: `#c9a84c` (gold), `#e0c97f` (gold light)
  - Neutral: `#f7f5f0` (offwhite), `#ede8dd` (cream), `#2c2c2c` (text)
- **Fonts**: Playfair Display (headings), Raleway (body), Cormorant Garamond (accents)
- **Features**: Video backgrounds, scroll-reveal animations, IntersectionObserver, counter animations, smooth scroll, mobile hamburger menu
- **No CMS, no backend**

### 3.2 Stack Comparison

#### Stack A: Enhanced Static HTML/CSS/JS

| Factor | Rating | Detail |
|--------|--------|--------|
| Performance | ⭐⭐⭐⭐⭐ | Zero overhead, ~50KB total JS |
| SEO | ⭐⭐⭐⭐⭐ | Pure HTML, perfect crawlability |
| Design match | ⭐⭐⭐⭐⭐ | Identical technology to parent site |
| Content management | ⭐ | Manual HTML editing for 12+ phases × deep content = nightmare |
| DX (developer experience) | ⭐⭐ | No components, no reuse, copy-paste errors |
| Animation capability | ⭐⭐⭐⭐ | GSAP works great, manual integration |
| Scalability | ⭐ | Adding phases requires duplicating HTML blocks |
| Future extensibility | ⭐⭐ | Hard to add search, CMS, dynamic features |

**Verdict**: Too painful at scale. 12+ phases with deep content, comparison tables, and callout boxes would be 5,000+ lines of HTML. Unmaintainable.

#### Stack B: Next.js 15 (App Router + SSG)

| Factor | Rating | Detail |
|--------|--------|--------|
| Performance | ⭐⭐⭐⭐ | 90–98 Lighthouse; ~130KB JS shipped (React runtime) |
| SEO | ⭐⭐⭐⭐⭐ | Static export, full meta control, sitemap generation |
| Design match | ⭐⭐⭐⭐ | Tailwind can replicate design tokens; JSX differs from HTML |
| Content management | ⭐⭐⭐⭐⭐ | MDX, Contentlayer, rich component embedding |
| DX | ⭐⭐⭐⭐⭐ | React ecosystem, hot reload, TypeScript |
| Animation capability | ⭐⭐⭐⭐⭐ | Framer Motion + GSAP, first-class support |
| Scalability | ⭐⭐⭐⭐⭐ | Component-based, easy to add pages |
| Future extensibility | ⭐⭐⭐⭐⭐ | API routes, auth, e-commerce, anything |

**Versions**: Next.js 15.3+, React 19, Tailwind CSS 4.x, Framer Motion 12.x
**Weakness**: Ships React runtime (~130KB) for a content site that doesn't need client-side interactivity. Overkill.

#### Stack C: Astro 5 (⭐ RECOMMENDED)

| Factor | Rating | Detail |
|--------|--------|--------|
| Performance | ⭐⭐⭐⭐⭐ | 97–100 Lighthouse; 0–15KB JS shipped |
| SEO | ⭐⭐⭐⭐⭐ | Pure HTML output, automatic sitemap, perfect Core Web Vitals |
| Design match | ⭐⭐⭐⭐⭐ | `.astro` syntax IS HTML; outputs same structure as parent site |
| Content management | ⭐⭐⭐⭐⭐ | Content Collections with type-safe Zod schemas |
| DX | ⭐⭐⭐⭐ | Great, but smaller ecosystem than React |
| Animation capability | ⭐⭐⭐⭐⭐ | GSAP works natively; React "islands" for interactive components |
| Scalability | ⭐⭐⭐⭐⭐ | Content Collections scale to thousands of pages |
| Future extensibility | ⭐⭐⭐⭐ | Server Islands for dynamic features; React/Vue/Svelte components when needed |

**Versions**: Astro 5.10+, Tailwind CSS 4.x, GSAP 3.14+
**Why it wins**: Outputs the same HTML/CSS/JS as the parent site. Zero JavaScript by default — content site doesn't need React. Content Collections provide type-safe Markdown/MDX management. `.astro` syntax feels like writing HTML with superpowers.

#### Stack D: Hugo / 11ty

| Factor | Rating | Detail |
|--------|--------|--------|
| Performance | ⭐⭐⭐⭐⭐ | Near-zero JS, blazing build times |
| SEO | ⭐⭐⭐⭐⭐ | Pure HTML output |
| Design match | ⭐⭐⭐⭐ | Template-based, can match CSS |
| Content management | ⭐⭐⭐⭐ | Markdown with frontmatter |
| DX | ⭐⭐⭐ | Go templates (Hugo) or Nunjucks (11ty) — less intuitive |
| Animation capability | ⭐⭐ | Manual JS integration, no component model for islands |
| Scalability | ⭐⭐⭐⭐ | Good for content, weak for interactivity |
| Future extensibility | ⭐⭐ | Hard to add interactive features later |

**Verdict**: Great for blogs, weak for the interactive comparison tables, progressive disclosure accordions, and animated phase navigation this site needs.

### 3.3 Recommendation: Astro 5

**Primary stack**: Astro 5.10+ with Content Collections, Tailwind CSS 4.x, GSAP 3.14

**Justification**:
1. **Output compatibility**: Astro outputs plain HTML/CSS/JS — identical technology to the parent HumphreyPools site. Future integration is trivial (shared stylesheets, link between sites).
2. **Zero JS by default**: A content/educational site doesn't need React hydration. Astro ships 0KB JS for static content, adding JS only for interactive islands (accordions, comparison sliders).
3. **Content Collections**: Type-safe schemas with Zod validation ensure consistent frontmatter across 12+ phases. Content lives in Markdown/MDX files, not HTML templates.
4. **Performance**: Near-perfect Lighthouse scores out of the box. Critical for luxury brand perception (slow = cheap).
5. **GSAP compatibility**: GSAP 3.14 (now free for all uses) works natively in Astro `<script>` tags — same as the parent site's vanilla JS approach.
6. **Progressive enhancement**: When interactive features are needed (before/after sliders, filterable galleries), React or Svelte "islands" can be added without shipping a framework to every page.

---

## 4. Architecture Patterns & Design

### 4.1 Architecture: Static Site with Islands

```
┌─────────────────────────────────────────────┐
│                  Astro SSG                    │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐   │
│  │  Layout   │  │  Pages   │  │ Content  │   │
│  │ Components│  │ (.astro) │  │Collections│  │
│  └──────────┘  └──────────┘  └──────────┘   │
│        │              │             │         │
│        ▼              ▼             ▼         │
│  ┌────────────────────────────────────────┐  │
│  │         Static HTML/CSS Output          │  │
│  │   (matches parent site technology)      │  │
│  └────────────────────────────────────────┘  │
│        │                                      │
│  ┌─────┴──────┐  ┌───────────────────┐       │
│  │   GSAP     │  │  Interactive      │       │
│  │ Animations │  │  Islands (React)  │       │
│  │ (vanilla)  │  │  (hydrate:visible)│       │
│  └────────────┘  └───────────────────┘       │
└─────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────┐
│       Cloudflare Pages / CDN                  │
│  Static files + image optimization + caching  │
└─────────────────────────────────────────────┘
```

### 4.2 Information Architecture

```
/                           → Landing page (phase overview cards + hero)
/phases/                    → Phase index (timeline view, all 12 phases)
/phases/01-site-assessment/ → Deep dive: Site Assessment & Engineering
/phases/02-design/          → Deep dive: Design & 3D Visualization
/phases/03-permitting/      → Deep dive: Permitting & HOA
/phases/04-excavation/      → Deep dive: Excavation
/phases/05-plumbing/        → Deep dive: Plumbing Rough-In
/phases/06-steel-rebar/     → Deep dive: Steel & Rebar
/phases/07-electrical/      → Deep dive: Electrical Rough-In
/phases/08-gunite-shell/    → Deep dive: Gunite/Shotcrete Shell
/phases/09-tile-coping/     → Deep dive: Tile & Coping
/phases/10-interior-finish/ → Deep dive: Interior Finish
/phases/11-decking/         → Deep dive: Decking & Hardscape
/phases/12-startup/         → Deep dive: Equipment, Fill & Startup
/features/                  → Specialty features index
/features/spa/              → Spa deep dive
/features/fire-pits/        → Sunken fire pits
/features/fire-bowls/       → Fire bowls & features
/features/acrylic-windows/  → Acrylic pool windows
/features/outdoor-kitchen/  → Outdoor kitchens
/features/patio-covers/     → Patio covers & pergolas
/features/turf/             → Artificial turf
```

### 4.3 Content Schema (Astro Content Collections)

```typescript
// src/content.config.ts
import { defineCollection, z } from 'astro:content';
import { glob } from 'astro/loaders';

const phases = defineCollection({
  loader: glob({ pattern: "**/*.mdx", base: "./src/data/phases" }),
  schema: z.object({
    number: z.number(),                    // 1–12
    title: z.string(),                     // "Plumbing Rough-In"
    subtitle: z.string(),                  // Brief 1-line description
    duration: z.string(),                  // "1–2 days"
    heroImage: z.string(),                 // Path to hero image
    thumbnailImage: z.string(),            // Card thumbnail
    luxurySummary: z.string(),             // 2-line luxury vs budget summary
    proTip: z.string().optional(),         // Pro tip callout
    watchOut: z.string().optional(),       // Warning callout
    comparisonTable: z.array(z.object({    // Luxury vs Budget rows
      aspect: z.string(),
      luxury: z.string(),
      budget: z.string(),
    })),
  }),
});

const features = defineCollection({
  loader: glob({ pattern: "**/*.mdx", base: "./src/data/features" }),
  schema: z.object({
    title: z.string(),
    subtitle: z.string(),
    heroImage: z.string(),
    costRange: z.string(),
    keyConsideration: z.string(),
  }),
});
```

### 4.4 UX Pattern: Progressive Disclosure

**Landing Page** → Card grid of all 12 phases (numbered, with thumbnail + 1-line description)
**Phase Page** → Full deep dive with this structure:

```
┌──────────────────────────────────────────┐
│  PHASE HERO (full-width image + number)   │
│  "05 — Plumbing Rough-In"                │
└──────────────────────────────────────────┘
┌──────────────────────────────────────────┐
│  🏆 LUXURY vs 💰 BUDGET — At a Glance    │
│  Quick visual summary card (icons)        │
│  [See Full Comparison ↓]                  │
└──────────────────────────────────────────┘
┌──────────────────────────────────────────┐
│  OVERVIEW (alternating image/text blocks) │
│  What happens, why it matters, duration   │
└──────────────────────────────────────────┘
┌──────────────────────────────────────────┐
│  ⚠️ WATCH OUT callout                     │
│  Gold-bordered warning box                │
└──────────────────────────────────────────┘
┌──────────────────────────────────────────┐
│  DETAILED COMPARISON TABLE                │
│  Color-coded: gold (luxury) vs grey       │
│  Icons: ✅ ⚠️ ❌ for scanning             │
└──────────────────────────────────────────┘
┌──────────────────────────────────────────┐
│  ACCORDION: Deep Details                  │
│  ▸ Technical specifications               │
│  ▸ Questions to ask your builder          │
│  ▸ Common mistakes to avoid               │
│  ▸ What can go wrong                      │
└──────────────────────────────────────────┘
┌──────────────────────────────────────────┐
│  💡 PRO TIP callout                       │
│  Expert insider knowledge                 │
└──────────────────────────────────────────┘
┌──────────────────────────────────────────┐
│  NAVIGATION: ← Previous Phase | Next →    │
│  + Phase dots progress indicator          │
└──────────────────────────────────────────┘
```

### 4.5 Navigation Patterns

**Desktop**: Sticky left sidebar TOC showing all phase numbers with abbreviated titles. Current phase highlighted with gold accent bar. Scroll progress indicator.

**Mobile**: Sticky top progress bar with horizontal numbered dots (01...12). Current phase dot enlarged/gold-filled. Tap to jump. Collapses to "Phase 5 of 12" on small screens.

### 4.6 Design System (Shared with Parent Site)

```css
/* Design tokens — must match HumphreyPools site exactly */
:root {
  --color-primary: #0a1628;
  --color-primary-light: #142440;
  --color-accent: #c9a84c;
  --color-accent-light: #e0c97f;
  --color-white: #ffffff;
  --color-offwhite: #f7f5f0;
  --color-cream: #ede8dd;
  --color-text: #2c2c2c;
  --color-text-light: #6b6b6b;

  --font-heading: 'Playfair Display', Georgia, serif;
  --font-body: 'Raleway', 'Segoe UI', sans-serif;
  --font-accent: 'Cormorant Garamond', Georgia, serif;

  --container-max: 1280px;
  --section-padding: 100px 0;
}
```

**Callout Box Variants**:

| Type | Icon | Color | Use |
|------|------|-------|-----|
| 💡 Pro Tip | Lightbulb | Teal left-border, light blue BG | Expert advice |
| ⚠️ Watch Out | Warning | Gold left-border, light gold BG | Common mistakes, critical warnings |
| 📌 Did You Know | Info | Navy left-border, light grey BG | Interesting facts |
| 🏆 Luxury Difference | Trophy | Gold left-border, cream BG | What separates $250K from $80K |

---

## 5. Libraries, Frameworks & Dependencies

### 5.1 Core Framework

| Package | Version | Purpose | License |
|---------|---------|---------|---------|
| `astro` | ^5.10 | Static site generator | MIT |
| `@astrojs/mdx` | ^4.x | MDX support for rich content | MIT |
| `@astrojs/sitemap` | ^3.x | Automatic sitemap generation | MIT |
| `@astrojs/react` | ^4.x | React islands for interactive components | MIT |
| `sharp` | ^0.33 | Image optimization (Astro built-in) | Apache-2.0 |

### 5.2 Styling

| Package | Version | Purpose | License |
|---------|---------|---------|---------|
| `@astrojs/tailwind` | ^6.x | Tailwind CSS integration | MIT |
| `tailwindcss` | ^4.x | Utility-first CSS framework | MIT |

### 5.3 Animation & Interaction

| Package | Version | Purpose | License |
|---------|---------|---------|---------|
| `gsap` | ^3.14 | Scroll animations, parallax, reveals | **Free** (no-charge license since 2024) |
| `lenis` | ^1.3 | Smooth scroll (luxury feel) | MIT |

**Note**: GSAP became free for all uses (including commercial) in late 2024. No more paid license needed.

### 5.4 Interactive Islands (React, only where needed)

| Package | Version | Purpose | License |
|---------|---------|---------|---------|
| `react` | ^19.x | Interactive components (islands only) | MIT |
| `react-dom` | ^19.x | DOM rendering for islands | MIT |
| `react-compare-slider` | ^3.x | Before/after image comparison | MIT |

### 5.5 Content Authoring

| Package | Version | Purpose | License |
|---------|---------|---------|---------|
| `astro` Content Collections | built-in | Type-safe content management | MIT |
| `zod` | built-in | Schema validation for frontmatter | MIT |

### 5.6 Development & Testing

| Tool | Version | Purpose |
|------|---------|---------|
| `typescript` | ^5.7 | Type safety |
| `prettier` | ^3.x | Code formatting |
| `prettier-plugin-astro` | ^0.14 | Astro file formatting |
| `@playwright/test` | ^1.50 | E2E testing (visual regression) |
| `lighthouse` | CLI | Performance auditing |

### 5.7 Optional: CMS (Phase 2)

| Tool | Purpose | Complexity |
|------|---------|------------|
| **Decap CMS** (formerly Netlify CMS) | Git-based CMS, edits Markdown files | Low — add later |
| **Tina CMS** | Visual editing, Git-backed | Medium |
| **Notion as CMS** | Non-technical content authoring | Low (with Astro loader) |

**Recommendation**: Start without a CMS. Content lives in MDX files in the repo. Add Decap CMS in Phase 2 if non-developer editing is needed.

### 5.8 Image Sources (Placeholder Luxury Photography)

| Source | URL | Notes |
|--------|-----|-------|
| **Unsplash** | `unsplash.com/s/photos/luxury-pool` | Free, high-quality, commercial use OK |
| **Pexels** | `pexels.com/search/luxury%20pool/` | Free, no attribution required |
| **Pixabay** | `pixabay.com/images/search/luxury%20pool/` | Free, commercial use OK |

**Search terms for luxury imagery**: "luxury pool aerial", "infinity pool sunset", "pool construction gunite", "outdoor kitchen luxury", "fire pit pool", "travertine pool deck", "pool tile mosaic glass", "spa jets luxury"

---

## 6. Security & Infrastructure

### 6.1 Hosting & Deployment

| Platform | Free Tier | Bandwidth | Build Minutes | Custom Domain | Best For |
|----------|-----------|-----------|---------------|---------------|----------|
| **Cloudflare Pages** ⭐ | Unlimited sites | **Unlimited** | 500/month | ✅ Free SSL | **Best value — recommended** |
| Vercel | 100GB/month | 100GB/month | 6000/month | ✅ Free SSL | Next.js projects |
| Netlify | 100GB/month | 100GB/month | 300/month | ✅ Free SSL | Decap CMS integration |
| Azure Static Web Apps | 100GB/month | 100GB/month | Custom | ✅ Free SSL | Azure ecosystem |
| GitHub Pages | Unlimited | 100GB/month | 2000/month | ✅ Free SSL | Simplest setup |

**Recommendation**: **Cloudflare Pages** — unlimited bandwidth is critical for image/video-heavy luxury site. Free tier is genuinely unlimited. Global CDN with 300+ PoPs. Zero cost at any scale.

### 6.2 Infrastructure Cost Estimates

| Scale | Monthly Cost | Breakdown |
|-------|-------------|-----------|
| **Small** (< 10K visits/mo) | **$0** | Cloudflare Pages free tier + Google Fonts |
| **Medium** (10K–100K visits/mo) | **$0–$5** | Still free tier; optional custom domain ($12/yr) |
| **Large** (100K+ visits/mo) | **$0–$20** | Free tier handles this; optional Cloudflare Pro ($20/mo) for analytics |

### 6.3 Image Optimization Strategy

```
Source images (high-res JPG/PNG)
  → Astro <Image> component
    → Automatic WebP/AVIF conversion
    → Responsive srcset generation
    → Lazy loading with blur-up placeholders
    → CDN-cached at edge
```

- Serve 800px max on mobile, 2400px on desktop
- Use `loading="lazy"` on all below-fold images
- Hero images: preloaded with `fetchpriority="high"`
- Target: < 3 second initial load on 4G

### 6.4 Security Considerations

- **No backend, no database**: Static site = minimal attack surface
- **No user data collection**: No forms, no cookies, no tracking (initially)
- **Content Security Policy**: Strict CSP headers via Cloudflare
- **HTTPS**: Automatic via Cloudflare (free SSL)
- **No API keys exposed**: Static site has no secrets to leak
- **If contact form added later**: Use Cloudflare Workers or third-party (Formspree) — never expose email directly

---

## 7. Risks, Trade-offs & Open Questions

### 7.1 Technical Risks

| Risk | Severity | Mitigation |
|------|----------|------------|
| Content volume overwhelming | Medium | Start with 4 key phases, expand iteratively |
| Image/video assets too large | Medium | Astro image optimization + WebP/AVIF + lazy loading |
| GSAP animations hurting mobile perf | Low | Use `prefers-reduced-motion` media query; test on real devices |
| Astro learning curve | Low | `.astro` syntax is HTML-like; minimal ramp-up |
| Parent site integration friction | Low | Same CSS variables, same fonts, same design tokens — link between sites |

### 7.2 Trade-offs Made

| Decision | Trade-off | Why We Accept It |
|----------|-----------|------------------|
| Astro over Next.js | Smaller ecosystem, fewer tutorials | Perfect fit for content-heavy static site; no React runtime overhead |
| No CMS initially | Non-devs can't edit content | Reduces complexity; MDX in repo is simpler; add CMS later |
| Static over SSR | No personalization, no dynamic content | Educational content doesn't need it; better performance |
| Cloudflare over Vercel | Less Next.js-specific tooling | Unlimited free bandwidth matters for image-heavy site |
| GSAP over Framer Motion | Requires manual DOM management | Works without React; matches parent site's vanilla JS approach |

### 7.3 Open Questions for Stakeholders

1. **Content authoring**: Who will write the detailed phase content? SME (Ben/Nicole) or hired copywriter?
2. **Photography**: Will custom project photography be available, or start with stock and replace later?
3. **Video**: Any construction process video footage available for hero sections?
4. **Domain**: Separate domain (e.g., `learn.humphreyluxurypools.com`) or path on main site (`humphreyluxurypools.com/process`)?
5. **Contact integration**: Should deep-dive pages link to consultation booking, or just the main site?
6. **Analytics**: Google Analytics, Cloudflare Analytics, or Plausible (privacy-focused)?
7. **Future CMS**: Will non-developers need to update content regularly?

### 7.4 Decisions to Make Upfront vs. Defer

| Decide Now | Defer |
|-----------|-------|
| Tech stack (Astro) | CMS selection |
| Design tokens (match parent) | Analytics platform |
| Content structure / IA | Contact form approach |
| Hosting platform (Cloudflare) | Custom photography timeline |
| Phase content outline | SEO keyword strategy |

---

## 8. Implementation Recommendations

### 8.1 Phased Approach

#### Phase 1: MVP (2–3 weeks)
- Astro project setup with Tailwind CSS and shared design tokens
- Landing page with phase overview cards (all 12 phases)
- 4 complete phase deep-dive pages (choose highest-impact):
  - Phase 5: Plumbing (most dramatic luxury vs budget differences)
  - Phase 6: Steel/Rebar (structural integrity, visceral comparison)
  - Phase 8: Gunite Shell (signature of luxury pool construction)
  - Phase 10: Interior Finish (most visible to homeowner)
- Luxury vs budget comparison tables on each
- Mobile responsive, scroll animations
- Deploy to Cloudflare Pages

#### Phase 2: Full Content (2–3 weeks)
- Remaining 8 phase deep-dive pages
- All 6 specialty feature pages (fire pits, acrylic windows, etc.)
- Spa-specific content
- Phase navigation (previous/next + progress dots)
- Sticky sidebar TOC (desktop) and progress bar (mobile)

#### Phase 3: Polish & Integrate (1–2 weeks)
- GSAP animations (parallax, scroll-triggered reveals)
- Placeholder images replaced with custom photography
- Before/after comparison sliders (React islands)
- Link to/from parent HumphreyPools.com site
- SEO optimization, sitemap, Open Graph meta

#### Phase 4: Enhance (ongoing)
- Decap CMS integration for content editing
- Analytics setup
- Contact form / consultation booking
- Blog section for ongoing content marketing
- Video integration (construction process footage)

### 8.2 Quick Wins

1. **Phase overview cards on landing page** — immediately demonstrates content depth and builds trust
2. **Luxury vs Budget comparison tables** — unique differentiator, highly shareable/bookmarkable
3. **"Watch Out" and "Pro Tip" callout boxes** — scannable, high-value content that positions Humphrey as experts
4. **Mobile-first responsive design** — luxury homeowners browse on phones

### 8.3 Prototyping Recommended

- **Phase page layout**: Build one complete phase page (Plumbing) before templating all 12 — validate content depth, callout box design, comparison table layout, and mobile experience
- **Animation timing**: Prototype scroll animations on a single page to find the right "luxury" timing (subtle, not flashy)
- **Navigation**: Test sticky sidebar TOC vs. top progress dots with real users

### 8.4 Project Setup Commands

```bash
# Initialize Astro project
npm create astro@latest humphrey-process-guide -- --template minimal

# Add integrations
npx astro add tailwind mdx sitemap react

# Add animation libraries
npm install gsap lenis

# Add development tools
npm install -D prettier prettier-plugin-astro @playwright/test

# Start development
npm run dev
```

### 8.5 Recommended File Structure

```
humphrey-process-guide/
├── public/
│   ├── images/
│   │   ├── phases/          # Phase hero + content images
│   │   ├── features/        # Specialty feature images
│   │   └── shared/          # Logo, icons, patterns
│   └── fonts/               # Self-hosted if needed
├── src/
│   ├── components/
│   │   ├── layout/          # Header, Footer, Navigation
│   │   ├── ui/              # Callouts, Cards, Tables, Buttons
│   │   ├── phase/           # PhaseHero, PhaseNav, ComparisonTable
│   │   └── islands/         # React interactive components
│   ├── content.config.ts    # Content Collection schemas
│   ├── data/
│   │   ├── phases/          # MDX files for each phase
│   │   └── features/        # MDX files for specialty features
│   ├── layouts/
│   │   ├── BaseLayout.astro
│   │   ├── PhaseLayout.astro
│   │   └── FeatureLayout.astro
│   ├── pages/
│   │   ├── index.astro      # Landing page
│   │   ├── phases/
│   │   │   ├── index.astro  # Phase timeline/index
│   │   │   └── [...slug].astro  # Dynamic phase pages
│   │   └── features/
│   │       ├── index.astro
│   │       └── [...slug].astro
│   └── styles/
│       └── global.css        # Design tokens, base styles
├── astro.config.mjs
├── tailwind.config.mjs
├── tsconfig.json
└── package.json
```

---

## Appendix A: Domain Content Inventory

### Complete Phase Content — Luxury vs Budget Differences

This appendix documents the specific quality differences that will be written about for each phase. Each phase page should include this comparison data.

#### Phase 5: Plumbing — Key Comparison Points

| Aspect | Luxury ($250K+) | Budget (< $80K) | Failure Cost |
|--------|-----------------|-----------------|--------------|
| Pipe type | Schedule 40 rigid PVC (140–166 psi, 75+ yr life) | Flex PVC (degrades, pest-vulnerable) | $3K–$15K leak repair |
| Fittings | Schedule 40 pressure-rated | DWV fittings (NOT pressure rated) | Joint failure |
| Pipe sizing | Oversized for low friction loss | Minimum code size | Poor circulation, algae |
| Return lines | 6–8+ returns for even circulation | 2–3 returns (dead spots) | Algae, uneven chemistry |
| Pressure test | 30 psi for 24 hours, documented | Quick check or skipped | Undetected leaks |
| Red flag | — | Flex PVC coils on job site | — |

#### Phase 6: Steel/Rebar — Key Comparison Points

| Aspect | Luxury ($250K+) | Budget (< $80K) | Failure Cost |
|--------|-----------------|-----------------|--------------|
| Bar size | #4 (½") or #5 bars | #3 (⅜") bars | Structural weakness |
| Spacing | 6–8" on center | 10–12" on center | Cracking |
| Coverage | Double mat in deep end, spa, features | Single layer throughout | Shell failure |
| Tie wire | Every intersection tied | Skipped intersections | Rebar shifts during gunite |
| Chairs | 3" off soil for concrete coverage | Rebar on ground | Exposed steel, rust, staining |
| Inspection | Structural engineer + city inspector | City only (if at all) | Code violation |
| Red flag | — | Builder resists steel inspection | $50K–$100K rebuild |

#### Phase 8: Gunite Shell — Key Comparison Points

| Aspect | Luxury ($250K+) | Budget (< $80K) | Failure Cost |
|--------|-----------------|-----------------|--------------|
| Wall thickness | 8–12" | 6" or thinner spots | Cracking, leaks |
| Floor thickness | 10–12"+ | 8" or less | Structural failure |
| Compressive strength | ≥ 4,000 PSI (tested at 28 days) | Untested / unknown | Spalling, deterioration |
| Nozzleman | ACI/ASA certified | Whoever is available | Voids, weak spots |
| Curing | Wet-cure daily for 7+ days | Shortened or skipped | 40% strength reduction |
| Rebound removal | Cleared completely | Left in place | Weak bonding, delamination |
| Red flag | — | Visible voids or honeycombing | $30K–$80K+ shell replacement |

### Specialty Feature Content Summary

#### Acrylic Pool Windows
- Material: Acrylic (PMMA) — 17x impact resistance of glass, half the weight
- Thickness: 2"–12" depending on span and water depth (structural engineer required)
- Cost: $15K–$200K+ per window (size-dependent)
- Installation: Specialist fabricator, 2–4 month lead time, meticulous sealing
- Maintenance: Seal inspection every ~5 years, surface scratches can be repolished
- Lifespan: 20–30 years with proper care

#### Sunken Fire Pits
- Gas strongly preferred near pools (no embers/ash contaminating water)
- Minimum 10ft from pool edge (check local code)
- Drainage non-negotiable: crushed gravel base + French drain + slope to drain point
- Dual opposing vents (min 18 sq in each) for gas safety
- All non-combustible materials (no moisture-trapping stone like limestone)
- Accessible gas shut-off outside enclosure

#### Outdoor Kitchens
- Position 10–15ft from pool edge (out of splash zone)
- Luxury appliance brands: Lynx, Kalamazoo, Wolf, DCS, Alfresco
- Marine-grade 304/316 stainless steel required for outdoor exposure
- Countertops: Granite or quartzite (NOT engineered quartz — fades in UV)
- Foundation: Minimum 4" reinforced concrete slab, 6" under heavy appliances
- Drainage: Slope 1/4" per foot away from kitchen; French drain or channel drain

#### Patio Covers
- Materials: Aluminum powder-coat (30+ yr, low maintenance) vs wood cedar/redwood (10–30 yr, high maintenance)
- Luxury: Motorized louvered roofs (StruXure brand), integrated lighting/fans/heaters/audio
- Engineering: Must be designed for local wind/snow loads; footings per soil conditions
- Brands: StruXure, Paragon Outdoor, Structureworks (ICC certified)

#### Artificial Turf
- Luxury base: 4–6" multi-layer (crushed stone + decomposed granite, compacted in lifts)
- Drainage rate: ≥ 30 inches/hour
- Pool-specific: Chlorine/salt resistant, non-slip, barefoot-comfortable polyethylene
- Steel edging, laser grading, antimicrobial infill
- Budget shortcut: 2–3" single layer on unprepared soil → sinks, pools water, mold

---

*This Research.md is the authoritative reference for the Humphrey Process Guide project. All architecture and implementation decisions should trace back to findings documented here.*
