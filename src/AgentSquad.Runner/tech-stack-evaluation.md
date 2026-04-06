# Technology Stack Evaluation: Luxury Pool Construction Educational Website

> **Date:** July 2025 | **Purpose:** Select the optimal stack for a content-heavy, visually rich,
> luxury-feel informational website with progressive disclosure UX.

---

## Table of Contents

1. [Requirements Summary](#1-requirements-summary)
2. [Stack 1: Enhanced Static HTML/CSS/JS](#2-stack-1-enhanced-static-htmlcssjs)
3. [Stack 2: Next.js (SSG)](#3-stack-2-nextjs-ssg)
4. [Stack 3: Astro](#4-stack-3-astro)
5. [Stack 4: Hugo / Eleventy](#5-stack-4-hugo--eleventy)
6. [Head-to-Head Comparison Matrix](#6-head-to-head-comparison-matrix)
7. [Animation Libraries Deep-Dive](#7-animation-libraries-deep-dive)
8. [Image Sourcing Strategy](#8-image-sourcing-strategy)
9. [Hosting Platform Comparison](#9-hosting-platform-comparison)
10. [CMS Options for Content Editing](#10-cms-options-for-content-editing)
11. [Final Recommendation](#11-final-recommendation)

---

## 1. Requirements Summary

| Requirement                | Detail                                                              |
|---------------------------|---------------------------------------------------------------------|
| **Visual Identity**       | Dark navy `#0a1628`, gold `#c9a84c`, cream tones                   |
| **Typography**            | Playfair Display, Raleway, Cormorant Garamond (Google Fonts)       |
| **UX Pattern**            | Progressive disclosure: overview → click to drill into details      |
| **Content Scale**         | 10+ major sections, each with sub-sections, tables, images          |
| **Animations**            | Smooth scroll, parallax, video backgrounds, fade-in reveals         |
| **Performance**           | Fast loading, Lighthouse 90+, mobile-responsive                    |
| **SEO**                   | Full SSR/SSG HTML, meta tags, structured data, sitemap              |
| **Content Updates**       | Easy for non-developers if possible                                 |
| **Integration**           | Must share design DNA with existing static HTML/CSS/JS parent site  |
| **Budget**                | Minimize hosting costs; free tier preferred                         |

---

## 2. Stack 1: Enhanced Static HTML/CSS/JS

### What It Is
Pure HTML files with CSS stylesheets and vanilla JavaScript — the same approach as the existing
parent site. No build step, no framework, no compilation.

### Specific "Stack"
```
HTML5 + CSS3 (custom properties) + Vanilla ES6+ JavaScript
Libraries loaded via CDN:
  - GSAP 3.14.x (ScrollTrigger, ScrollSmoother)
  - AOS 2.3.4 (simpler scroll animations)
  - Lenis 1.3.x (smooth scroll)
```

### Progressive Disclosure Implementation
```javascript
// Vanilla JS accordion/expand pattern
document.querySelectorAll('[data-expand-trigger]').forEach(trigger => {
  trigger.addEventListener('click', () => {
    const target = document.getElementById(trigger.dataset.expandTarget);
    target.classList.toggle('expanded');
    // Animate with GSAP for smooth height transition
    gsap.to(target, {
      height: target.classList.contains('expanded') ? 'auto' : 0,
      duration: 0.5, ease: 'power2.inOut'
    });
  });
});
```

### Evaluation

| Criterion                     | Rating | Notes                                                    |
|------------------------------|--------|----------------------------------------------------------|
| **Performance**              | ★★★★★  | Zero JS framework overhead. Smallest possible payload.   |
| **Lighthouse Score**         | 95-100 | No hydration, no unused JS. Pure HTML = fastest FCP/LCP. |
| **Bundle Size**              | ~50KB  | Only GSAP (~23KB gz) + AOS (~6KB gz) + custom CSS/JS.   |
| **SEO**                      | ★★★★★  | Pure HTML is inherently SEO-perfect. Full control.       |
| **Animations/Interactions**  | ★★★★☆  | GSAP handles everything, but wiring is manual.           |
| **Developer Experience**     | ★★☆☆☆  | Copy-paste repetition, no components, error-prone.       |
| **Content Authoring**        | ★☆☆☆☆  | Edit raw HTML. High risk of breaking layout.             |
| **Hosting/Deployment**       | ★★★★★  | Any web server, GitHub Pages, S3. $0/month.              |
| **Future Extensibility**     | ★★☆☆☆  | Adding features = more spaghetti. No module system.      |
| **Learning Curve**           | ★★★★★  | Everyone knows HTML/CSS/JS.                              |

### Pros
- **Zero build complexity** — edit a file, upload it, done
- **Maximum performance** — nothing to parse or hydrate
- **Perfect parent site integration** — same technology, shared CSS
- **Free hosting anywhere** — GitHub Pages, Netlify, any web server
- **No dependency risk** — no npm vulnerabilities, no breaking upgrades

### Cons
- **Content management nightmare** — 10+ sections × sub-sections = dozens of HTML files with
  repeated header/footer/nav markup. One nav change → edit every file.
- **No templating** — every section repeats the same layout boilerplate
- **No image optimization** — manual responsive images, no automatic WebP/AVIF
- **Progressive disclosure at scale** — custom JS for every expandable section gets messy
- **Maintenance burden grows exponentially** with content volume

### When This Makes Sense
- ✅ Site has ≤5 pages with minimal content changes
- ✅ Developer maintains it personally and doesn't mind manual work
- ✅ Already exists and budget is $0 for migration
- ❌ 10+ major sections with deep content = too much repetition
- ❌ Multiple people need to update content

---

## 3. Stack 2: Next.js (SSG)

### Current Versions (July 2025)
```
next                    ^15.3.x  (or 16.x latest)
react                   ^19.x
react-dom               ^19.x
tailwindcss             ^4.1.x
framer-motion           ^11.x
@next/mdx               ^15.x
@mdx-js/react           ^3.x
gsap                    ^3.14.x
lenis                   ^1.3.x
next-seo                ^7.x     (or next-sitemap)
sharp                   ^0.33.x  (image optimization)
contentlayer2           ^0.5.x   (if using Contentlayer for MDX)
```

> **Note:** Next.js 16 was released mid-2025 with Turbopack as default bundler. Next.js 15.x
> remains widely deployed and stable. Either works for SSG.

### Architecture
```
project/
├── app/                        # App Router (Next.js 15+)
│   ├── layout.tsx              # Root layout with fonts, nav, footer
│   ├── page.tsx                # Homepage hero
│   ├── phases/
│   │   ├── page.tsx            # Phase overview (progressive disclosure)
│   │   └── [slug]/
│   │       └── page.tsx        # Deep-dive into each phase
│   └── sections/
│       └── [slug]/page.tsx     # Each major content section
├── components/
│   ├── LuxuryHero.tsx          # Reusable video hero
│   ├── PhaseCard.tsx           # Expandable phase card
│   ├── ContentTable.tsx        # Styled data table
│   ├── ParallaxSection.tsx     # Parallax wrapper
│   └── ProgressiveDisclosure.tsx
├── content/                    # MDX files (the actual content)
│   ├── phases/
│   │   ├── design.mdx
│   │   ├── excavation.mdx
│   │   └── finishing.mdx
│   └── sections/
│       ├── costs.mdx
│       └── materials.mdx
├── styles/
│   └── globals.css             # Design tokens matching parent site
├── public/
│   └── images/                 # Optimized images
├── tailwind.config.ts
└── next.config.mjs             # output: 'export' for static
```

### Design Token Integration (Tailwind CSS)
```css
/* globals.css — Tailwind v4 CSS-first config */
@import "tailwindcss";

@theme {
  --color-navy: #0a1628;
  --color-navy-light: #1a2a4a;
  --color-gold: #c9a84c;
  --color-gold-light: #d4b968;
  --color-cream: #f5f0e8;
  --color-cream-dark: #e8e0d0;

  --font-display: 'Playfair Display', serif;
  --font-body: 'Raleway', sans-serif;
  --font-accent: 'Cormorant Garamond', serif;
}
```

### MDX Content Example
```mdx
---
title: "Pool Design & Engineering"
phase: 1
icon: "drafting-compass"
summary: "From dream to blueprint — the design phase shapes everything."
---

import { CostTable } from '@/components/CostTable'
import { BeforeAfter } from '@/components/BeforeAfter'

# Pool Design & Engineering

The design phase is where your vision takes architectural form. A luxury pool
isn't just a hole with water — it's an **engineered aquatic environment**.

## What to Expect

<CostTable data={[
  { item: "Architectural Design", range: "$3,000 - $15,000" },
  { item: "Engineering Plans", range: "$2,000 - $8,000" },
  { item: "Permit Fees", range: "$500 - $3,000" },
]} />

<BeforeAfter before="/images/empty-yard.jpg" after="/images/pool-render.jpg" />
```

### Static Export
```javascript
// next.config.mjs
const nextConfig = {
  output: 'export',        // Full static HTML export
  images: {
    unoptimized: false,     // Use sharp for build-time optimization
  },
  trailingSlash: true,
};
```

### Evaluation

| Criterion                     | Rating | Notes                                                        |
|------------------------------|--------|--------------------------------------------------------------|
| **Performance**              | ★★★★☆  | Static export is fast, but React runtime adds ~85KB gz.      |
| **Lighthouse Score**         | 90-98  | Excellent with static export. Hydration adds slight penalty. |
| **Bundle Size**              | ~130KB | React (~45KB) + Next runtime (~40KB) + app code + GSAP.     |
| **SEO**                      | ★★★★★  | generateStaticParams, full HTML at build, metadata API.      |
| **Animations/Interactions**  | ★★★★★  | Framer Motion + GSAP = unlimited possibilities in React.     |
| **Developer Experience**     | ★★★★★  | Component reuse, TypeScript, hot reload, massive ecosystem.  |
| **Content Authoring**        | ★★★★☆  | MDX is great for devs, okay for non-devs with CMS overlay.  |
| **Hosting/Deployment**       | ★★★★★  | Static export → any host. Or Vercel for zero-config.         |
| **Future Extensibility**     | ★★★★★  | Add forms, auth, API routes, e-commerce — anything.          |
| **Learning Curve**           | ★★★☆☆  | React + Next.js + Tailwind = significant learning required.  |

### Pros
- **Component architecture** — build `<PhaseCard>`, `<LuxuryTable>`, `<ParallaxHero>` once,
  reuse everywhere. Change the design in one place.
- **MDX is powerful** — write content in Markdown, embed React components inline
- **Image optimization** — `next/image` with automatic WebP/AVIF, lazy loading, blur placeholders
- **Tailwind design tokens** — exact match to parent site colors, fonts, spacing
- **TypeScript safety** — catch errors before they reach production
- **Massive ecosystem** — thousands of components, libraries, tutorials
- **Static export** — `next build && next export` produces plain HTML/CSS/JS files

### Cons
- **React runtime overhead** — ~85KB gzipped even for a content site that barely needs interactivity
- **Build complexity** — Node.js, npm, build pipeline needed
- **Overkill for content** — React's virtual DOM solves problems this site doesn't have
- **Hydration mismatch risk** — content renders as HTML but then React "hydrates" it,
  occasionally causing flickers
- **Learning curve** — React + JSX + hooks + Next.js App Router + Tailwind = a lot to learn
- **Content authoring** — MDX requires understanding frontmatter and import syntax;
  non-developers need a CMS layer (Decap CMS, TinaCMS) on top

---

## 4. Stack 3: Astro ⭐ RECOMMENDED

### Current Versions (July 2025)
```
astro                   ^5.10.x  (or 6.x latest)
@astrojs/tailwind       ^6.x
@astrojs/mdx            ^4.x
@astrojs/sitemap        ^4.x
@astrojs/react          ^4.x     (only for interactive islands)
astro-seo               ^0.8.x
gsap                    ^3.14.x
lenis                   ^1.3.x
sharp                   ^0.33.x  (built-in image optimization)
```

### Architecture
```
project/
├── src/
│   ├── layouts/
│   │   └── BaseLayout.astro      # Shared HTML shell (nav, footer, fonts)
│   ├── pages/
│   │   ├── index.astro           # Homepage with hero, overview
│   │   ├── phases/
│   │   │   ├── index.astro       # Phase overview grid
│   │   │   └── [slug].astro      # Dynamic phase detail pages
│   │   └── sections/
│   │       └── [slug].astro
│   ├── components/
│   │   ├── LuxuryHero.astro      # Zero-JS hero component
│   │   ├── PhaseCard.astro       # Expandable card (CSS-only or minimal JS)
│   │   ├── ParallaxSection.astro # GSAP-powered parallax
│   │   ├── VideoBackground.astro
│   │   └── interactive/
│   │       └── ExpandableContent.tsx  # React island (only when needed)
│   ├── content/
│   │   ├── phases/
│   │   │   ├── design.mdx
│   │   │   ├── excavation.mdx
│   │   │   └── finishing.mdx
│   │   └── sections/
│   │       ├── costs.mdx
│   │       └── materials.mdx
│   └── styles/
│       └── global.css
├── content.config.ts             # Astro Content Layer definitions
├── public/
│   └── images/
├── astro.config.mjs
└── tailwind.config.ts
```

### Content Collections (Astro 5+)
```typescript
// content.config.ts
import { defineCollection, z } from 'astro:content';
import { glob } from 'astro/loaders';

const phases = defineCollection({
  loader: glob({ pattern: '**/*.mdx', base: './src/content/phases' }),
  schema: z.object({
    title: z.string(),
    phase: z.number(),
    icon: z.string(),
    summary: z.string(),
    heroImage: z.string().optional(),
    duration: z.string().optional(),
    costRange: z.string().optional(),
  }),
});

const sections = defineCollection({
  loader: glob({ pattern: '**/*.mdx', base: './src/content/sections' }),
  schema: z.object({
    title: z.string(),
    order: z.number(),
    category: z.string(),
  }),
});

export const collections = { phases, sections };
```

### Zero-JS by Default, Islands When Needed
```astro
---
// src/pages/phases/index.astro
import BaseLayout from '@/layouts/BaseLayout.astro';
import PhaseCard from '@/components/PhaseCard.astro';
import { getCollection } from 'astro:content';

const phases = await getCollection('phases');
const sorted = phases.sort((a, b) => a.data.phase - b.data.phase);
---

<BaseLayout title="Construction Phases">
  <section class="phase-grid">
    {sorted.map(phase => (
      <PhaseCard
        title={phase.data.title}
        summary={phase.data.summary}
        phase={phase.data.phase}
        href={`/phases/${phase.id}`}
      />
    ))}
  </section>

  <!-- Interactive island: only this component ships JS -->
  <ExpandableTimeline client:visible phases={sorted} />
</BaseLayout>
```

### Island Architecture Explained
```
┌─────────────────────────────────────────────────────────────┐
│  Full Page (Static HTML — 0KB JavaScript)                   │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐   │
│  │  Header / Navigation          (Astro component, 0 JS)│   │
│  └──────────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │  Hero with Video Background   (Astro + <video> tag)  │   │
│  └──────────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │  Phase Overview Cards          (Astro, CSS-only hover)│   │
│  └──────────────────────────────────────────────────────┘   │
│  ┌──────────────────────┐                                    │
│  │  Interactive Timeline │ ← React Island (~8KB JS)         │
│  │  (client:visible)     │   Only loads when scrolled into   │
│  └──────────────────────┘   view. Rest of page = 0 JS.     │
│  ┌──────────────────────────────────────────────────────┐   │
│  │  Content Tables / Text         (Astro, 0 JS)         │   │
│  └──────────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │  Footer                        (Astro component, 0 JS)│   │
│  └──────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### Evaluation

| Criterion                     | Rating | Notes                                                        |
|------------------------------|--------|--------------------------------------------------------------|
| **Performance**              | ★★★★★  | Zero JS by default. Only interactive islands ship JS.        |
| **Lighthouse Score**         | 97-100 | Near-perfect. HTML-first with no framework runtime.          |
| **Bundle Size**              | ~15KB  | Base page: 0KB JS. Add GSAP for scroll pages (~23KB gz).    |
| **SEO**                      | ★★★★★  | Pure HTML output, automatic sitemap, full head control.      |
| **Animations/Interactions**  | ★★★★☆  | GSAP works perfectly. Use React islands for complex widgets. |
| **Developer Experience**     | ★★★★★  | .astro files feel like HTML. Content Collections are typed.  |
| **Content Authoring**        | ★★★★☆  | MDX with schema validation. Add Decap CMS for non-devs.     |
| **Hosting/Deployment**       | ★★★★★  | Static output → any host. First-class Netlify/Vercel/CF.     |
| **Future Extensibility**     | ★★★★☆  | Add React/Vue/Svelte components as needed. SSR available.    |
| **Learning Curve**           | ★★★★☆  | .astro syntax is easy. Content Collections take some setup.  |

### Pros
- **Best performance for content sites** — ships zero JavaScript unless you explicitly opt in
- **Content Collections** — type-safe Markdown/MDX with Zod schema validation; build fails if
  content doesn't match schema (catches errors before deploy)
- **Island architecture** — use React/Vue/Svelte only for truly interactive parts
- **Feels like writing HTML** — `.astro` files use familiar HTML/CSS syntax with a frontmatter
  script block; much easier to learn than JSX
- **Built-in image optimization** — `<Image>` component with WebP/AVIF, responsive sizes, lazy loading
- **5x faster builds** than Astro 4 for Markdown-heavy sites (Content Layer API)
- **Parent site integration** — output is plain HTML/CSS/JS; can share stylesheets directly
- **Vite-powered** — instant hot reload during development

### Cons
- **Animations require vanilla JS or islands** — no built-in animation system like Framer Motion;
  GSAP works but needs `<script>` tags in .astro files or React islands
- **Smaller ecosystem than React/Next.js** — fewer ready-made components
- **Less familiar** — .astro syntax is new to most developers (though easy to learn)
- **Complex interactivity needs React islands** — if many sections need expand/collapse with
  state management, you end up shipping React anyway

---

## 5. Stack 4: Hugo / Eleventy

### Current Versions (July 2025)
```
Hugo:     v0.158.x (Go binary, no npm needed)
Eleventy: @11ty/eleventy ^3.1.x (Node.js)
```

### Hugo Architecture
```
project/
├── content/
│   ├── phases/
│   │   ├── design.md
│   │   ├── excavation.md
│   │   └── _index.md            # Section listing page
│   └── sections/
│       ├── costs.md
│       └── materials.md
├── layouts/
│   ├── _default/
│   │   ├── baseof.html          # Base template
│   │   ├── list.html            # Section listing
│   │   └── single.html          # Individual page
│   ├── partials/
│   │   ├── header.html
│   │   ├── footer.html
│   │   ├── phase-card.html
│   │   └── luxury-table.html
│   └── shortcodes/
│       ├── cost-table.html      # {{ < cost-table > }}
│       └── before-after.html
├── assets/
│   └── css/
│       └── main.css
├── static/
│   └── images/
└── config.toml
```

### Eleventy Architecture
```
project/
├── src/
│   ├── _data/                   # Global data files (JSON/JS)
│   │   └── phases.json
│   ├── _includes/
│   │   ├── layouts/
│   │   │   └── base.njk         # Nunjucks base layout
│   │   └── components/
│   │       ├── phase-card.njk
│   │       └── luxury-table.njk
│   ├── phases/
│   │   ├── design.md
│   │   └── excavation.md
│   └── sections/
│       ├── costs.md
│       └── materials.md
├── .eleventy.js                 # Config (ESM in v3)
└── package.json
```

### Evaluation

| Criterion                     | Rating | Notes                                                       |
|------------------------------|--------|-------------------------------------------------------------|
| **Performance**              | ★★★★★  | Pure HTML output. Zero runtime JS (same as static HTML).    |
| **Lighthouse Score**         | 97-100 | Identical to hand-written HTML but with templating.         |
| **Bundle Size**              | ~0KB   | No framework JS. Only what you add (GSAP, AOS).            |
| **SEO**                      | ★★★★★  | Pure HTML, auto sitemaps (Hugo built-in), RSS feeds.        |
| **Animations/Interactions**  | ★★★☆☆  | Must wire GSAP/AOS manually like static HTML. No components.|
| **Developer Experience**     | ★★★☆☆  | Hugo's Go templates are quirky. 11ty is better (Nunjucks).  |
| **Content Authoring**        | ★★★★☆  | Markdown-native. Add Decap CMS for GUI editing.            |
| **Hosting/Deployment**       | ★★★★★  | Static output → any host. Hugo builds in <1 second.        |
| **Future Extensibility**     | ★★☆☆☆  | Hard to add interactive features. No component model.       |
| **Learning Curve**           | ★★★☆☆  | Hugo: steep (Go templates). 11ty: moderate (Nunjucks/JS).  |

### Hugo Pros
- **Fastest builds** — 1,000 pages in ~2 seconds (Go-compiled)
- **Batteries included** — image processing, menus, i18n, taxonomies, sitemaps built-in
- **Single binary** — no Node.js, no npm, no dependencies
- **300+ themes** — many polished, professional templates
- **Markdown-native** — shortcodes for custom elements within Markdown

### Hugo Cons
- **Go template syntax** — `{{ .Title }}`, `{{ range .Pages }}` is unfamiliar and error-prone
- **No component model** — partials are include-based, not composable like React/Astro components
- **Interactive features** — shortcodes can't do client-side interactivity; must add raw JS
- **Progressive disclosure** — requires custom JavaScript; Hugo has no opinion on this
- **Harder to match luxury animations** — no built-in way to scope GSAP animations to components

### Eleventy Pros
- **JavaScript-based** — familiar ecosystem, npm packages available
- **Template flexibility** — Nunjucks, Liquid, Handlebars, Markdown, HTML all work
- **Data cascade** — powerful data-merging system for content
- **Plugin ecosystem** — 200+ community plugins
- **Incremental adoption** — start from plain HTML, gradually add templating

### Eleventy Cons
- **Slower builds** — 50-200ms/page vs Hugo's 1-5ms/page
- **Minimal core** — many features require plugins (image optimization, etc.)
- **Same interactivity limitations as Hugo** — template-based, not component-based
- **Smaller community** than Hugo, Next.js, or Astro

---

## 6. Head-to-Head Comparison Matrix

| Criterion                  | Static HTML | Next.js SSG | Astro ⭐    | Hugo / 11ty  |
|---------------------------|-------------|-------------|-------------|--------------|
| **JS Shipped (base page)**| ~50KB (GSAP)| ~130KB      | ~0-15KB     | ~50KB (GSAP) |
| **Lighthouse Performance**| 95-100      | 90-98       | 97-100      | 97-100       |
| **First Contentful Paint**| ~0.8s       | ~1.2s       | ~0.8s       | ~0.8s        |
| **Build Time (100 pages)**| N/A         | ~15s        | ~5s         | <1s (Hugo)   |
| **Component Reuse**       | ❌ None     | ✅ Full     | ✅ Full     | ⚠️ Partials  |
| **Type Safety**           | ❌          | ✅ TS       | ✅ TS+Zod   | ❌           |
| **Content Collections**   | ❌          | ⚠️ MDX+CL  | ✅ Built-in | ✅ Native MD |
| **Image Optimization**    | ❌ Manual   | ✅ Built-in | ✅ Built-in | ✅ Hugo only |
| **Progressive Disclosure**| ⚠️ Manual  | ✅ React    | ✅ Islands  | ⚠️ Manual   |
| **Animations (GSAP)**     | ✅ Direct   | ✅ + Framer | ✅ Direct   | ✅ Direct    |
| **Video Backgrounds**     | ✅          | ✅          | ✅          | ✅           |
| **Parallax/Scroll FX**    | ✅ Manual   | ✅ Framer   | ✅ GSAP     | ✅ Manual    |
| **CMS Integration**       | ⚠️ Limited | ✅ Many     | ✅ Many     | ✅ Decap     |
| **Parent Site Compat**    | ✅ Same     | ⚠️ Similar | ✅ Same CSS | ⚠️ Similar  |
| **Non-Dev Content Edit**  | ❌          | ⚠️ +CMS    | ⚠️ +CMS    | ⚠️ +CMS     |
| **Learning Curve**        | None        | High        | Low-Med     | Med-High     |
| **Ecosystem Size**        | N/A         | Massive     | Growing     | Large (Hugo) |

---

## 7. Animation Libraries Deep-Dive

### GSAP 3.14.x ⭐ RECOMMENDED for this project

| Aspect             | Detail                                                             |
|--------------------|--------------------------------------------------------------------|
| **Version**        | 3.14.x (npm: `gsap`)                                             |
| **License**        | **Now 100% free** (including all former "Club" plugins). Funded by Webflow. |
| **Size**           | ~23KB gzipped (core). Plugins are tree-shakeable.                 |
| **Key Plugins**    | ScrollTrigger (scroll-based), ScrollSmoother (buttery smooth),    |
|                    | SplitText (text reveals), MorphSVG, DrawSVG                      |
| **Best For**       | Parallax, pinned scroll sections, timeline sequencing, video sync |
| **Framework**      | Works with everything: vanilla, React, Astro, Vue, etc.          |
| **React Usage**    | `@gsap/react` package with `useGSAP()` hook                      |

```javascript
// Luxury parallax example
import gsap from 'gsap';
import { ScrollTrigger } from 'gsap/ScrollTrigger';
gsap.registerPlugin(ScrollTrigger);

gsap.to('.hero-image', {
  yPercent: -30,
  ease: 'none',
  scrollTrigger: {
    trigger: '.hero-section',
    start: 'top top',
    end: 'bottom top',
    scrub: true
  }
});
```

### Framer Motion 11.x

| Aspect             | Detail                                                             |
|--------------------|--------------------------------------------------------------------|
| **Version**        | 11.x (npm: `framer-motion`)                                      |
| **License**        | MIT (fully open source)                                           |
| **Size**           | ~32-46KB gzipped                                                  |
| **Key Features**   | AnimatePresence, layout animations, gesture support, variants     |
| **Best For**       | React UI animations, enter/exit transitions, drag interactions    |
| **Framework**      | **React only**                                                    |
| **Scroll**         | Basic `useScroll` hook; not as powerful as GSAP ScrollTrigger     |

**Verdict:** Great for React UI micro-interactions. GSAP is better for the cinematic scroll
effects this luxury site needs. In Next.js, use both together.

### Lenis 1.3.x

| Aspect             | Detail                                                            |
|--------------------|--------------------------------------------------------------------|
| **Version**        | 1.3.21 (npm: `lenis`)                                            |
| **License**        | MIT                                                               |
| **Size**           | ~8KB gzipped                                                      |
| **What It Does**   | Replaces native scroll with smooth, inertia-based scrolling       |
| **Best For**       | "Luxury feel" smooth scrolling that enhances GSAP ScrollTrigger   |
| **Works With**     | Everything. Pairs perfectly with GSAP.                            |

```javascript
import Lenis from 'lenis';
const lenis = new Lenis({ autoRaf: true });
// Integrate with GSAP ScrollTrigger
lenis.on('scroll', ScrollTrigger.update);
```

### AOS 2.3.4

| Aspect             | Detail                                                            |
|--------------------|--------------------------------------------------------------------|
| **Version**        | 2.3.4 stable (npm: `aos`)                                        |
| **License**        | MIT                                                               |
| **Size**           | ~6KB gzipped                                                      |
| **What It Does**   | Simple scroll-triggered reveal animations via data attributes     |
| **Best For**       | Quick fade/slide-in effects without complex setup                 |
| **Limitation**     | No timeline control, no parallax, no scrubbing                    |

**Verdict:** Good for simple reveals. For this project's luxury feel, GSAP + Lenis is the better
choice. AOS could supplement for simple sections.

### Recommended Animation Stack for This Project
```
Primary:  GSAP 3.14.x + ScrollTrigger + ScrollSmoother
Scroll:   Lenis 1.3.x (smooth scrolling foundation)
Simple:   AOS 2.3.4 (for basic fade-in reveals on simpler pages)
React:    Framer Motion 11.x (only if using Next.js, for UI transitions)
```

---

## 8. Image Sourcing Strategy

### Free Stock Photo Sources

| Source        | Search Terms                                           | Quality | Notes                    |
|---------------|-------------------------------------------------------|---------|--------------------------|
| **Unsplash**  | "luxury pool", "infinity pool", "pool at night",      | ★★★★★  | High-res, free, no attr  |
|               | "outdoor living", "pool landscaping", "resort pool"    |         | required (but nice)      |
| **Pexels**    | "swimming pool luxury", "backyard pool",              | ★★★★☆  | Free, no attribution     |
|               | "pool construction", "pool lighting"                   |         |                          |
| **Pixabay**   | "luxury swimming pool", "pool design"                 | ★★★☆☆  | Mixed quality, free      |
| **Freepik**   | "luxury pool mockup", "pool blueprint"                | ★★★★☆  | Some require attribution |

### Specific Unsplash Collections & Photographers
- Search: `unsplash.com/s/photos/luxury-pool`
- Search: `unsplash.com/s/photos/infinity-pool`
- Search: `unsplash.com/s/photos/pool-construction`
- Search: `unsplash.com/s/photos/outdoor-living-space`
- Search: `unsplash.com/s/photos/pool-night-lighting`
- Search: `unsplash.com/s/photos/resort-pool`
- Search: `unsplash.com/s/photos/hot-tub-luxury`
- Search: `unsplash.com/s/photos/pool-waterfall`

### Image Categories Needed

| Category                  | Description                                  | Suggested Search               |
|--------------------------|----------------------------------------------|---------------------------------|
| **Hero Images**          | Wide, cinematic luxury pool shots            | "infinity pool sunset aerial"  |
| **Phase: Design**        | Blueprints, 3D renders, architecture         | "pool blueprint", "pool CAD"   |
| **Phase: Excavation**    | Heavy equipment, earth moving                | "excavation pool construction" |
| **Phase: Plumbing**      | Pipes, equipment pads                        | "pool plumbing equipment"      |
| **Phase: Shell**         | Gunite/shotcrete, rebar, steel               | "pool gunite rebar"            |
| **Phase: Finishing**     | Tile, coping, decking, plaster               | "pool tile mosaic luxury"      |
| **Phase: Landscaping**   | Plants, outdoor kitchen, fire features       | "pool landscaping luxury"      |
| **Materials**            | Natural stone, glass tile samples            | "natural stone pool deck"      |
| **Before/After**         | Transformation shots                         | "pool before after"            |
| **Lifestyle**            | People enjoying luxury pools                 | "luxury pool lifestyle"        |
| **Video Background**     | Aerial pool footage, water surface           | Pexels Videos: "pool aerial"   |

### Video Backgrounds (Free)
- **Pexels Videos** — `pexels.com/search/videos/swimming-pool/`
- **Coverr** — `coverr.co` (free stock video, search "pool", "water")
- **Mixkit** — `mixkit.co/free-stock-video/pool/` (free, high quality)

### Placeholder Image Strategy
For development, use dynamic placeholders:
```html
<!-- Unsplash Source (random luxury pool) -->
<img src="https://source.unsplash.com/1200x800/?luxury-pool" alt="Luxury pool">

<!-- Specific curated Unsplash photos by ID -->
<img src="https://images.unsplash.com/photo-1572331165267-854da2b021b1?w=1200" alt="Pool">
```

---

## 9. Hosting Platform Comparison

### Free Tier Comparison (July 2025)

| Platform               | Bandwidth   | Build Min/Mo | Custom Domains | Team Size | Best For         |
|-----------------------|-------------|--------------|----------------|-----------|------------------|
| **Cloudflare Pages**  | **Unlimited**| 500 builds  | 100/project    | Unlimited | High traffic     |
| **Vercel**            | 100 GB      | 6,000        | Unlimited      | 1         | Next.js projects |
| **Netlify**           | 100 GB      | 300          | Unlimited      | 1         | Jamstack + forms |
| **GitHub Pages**      | 100 GB      | N/A (CI/CD) | 1              | N/A       | Simple static    |
| **Azure Static Web**  | Limited     | Included     | 1 (free tier)  | 1         | Microsoft shops  |

### Cost at Scale

| Scenario               | Cloudflare  | Vercel Pro  | Netlify Pro | Azure       |
|-----------------------|-------------|-------------|-------------|-------------|
| **Free tier**         | $0/mo       | $0/mo       | $0/mo       | $0/mo       |
| **Pro tier**          | $5/mo (Workers)| $20/mo   | $19/mo      | ~$10/mo     |
| **10K visitors/mo**   | $0          | $0          | $0          | $0          |
| **100K visitors/mo**  | $0          | $0          | $0          | ~$5/mo      |
| **1M visitors/mo**    | $0          | ~$20/mo     | ~$19/mo     | ~$20/mo     |

### Recommendations by Stack

| Stack              | Primary Host          | Alternative           | Why                          |
|--------------------|-----------------------|-----------------------|------------------------------|
| **Static HTML**    | GitHub Pages          | Cloudflare Pages      | Zero config, free            |
| **Next.js**        | Vercel                | Cloudflare Pages      | Native Next.js support       |
| **Astro** ⭐       | Cloudflare Pages      | Netlify               | Unlimited BW, fast edge CDN  |
| **Hugo/11ty**      | Cloudflare Pages      | Netlify               | Free, fast builds            |

### Winner: Cloudflare Pages
- Unlimited bandwidth on free tier (unbeatable for content sites)
- Global CDN with 300+ PoPs
- Automatic HTTPS, DDoS protection
- Git integration (GitHub/GitLab)
- Workers for any server-side logic needed later

---

## 10. CMS Options for Content Editing

### Option A: Decap CMS (formerly Netlify CMS) ⭐ RECOMMENDED for non-dev editing

| Aspect              | Detail                                                          |
|---------------------|-----------------------------------------------------------------|
| **Type**            | Git-based, open-source headless CMS                            |
| **How It Works**    | Web UI at `/admin` commits Markdown to your Git repo           |
| **Authentication**  | GitHub OAuth, Netlify Identity, or custom                      |
| **Content Format**  | Markdown/MDX with frontmatter                                  |
| **Media**           | Upload images to repo or external media (Cloudinary, etc.)     |
| **Works With**      | Any SSG: Astro, Next.js, Hugo, 11ty                           |
| **Setup**           | Add `admin/index.html` + `config.yml` to your project          |
| **Cost**            | Free (open source)                                             |
| **Drawback**        | No real-time preview of MDX components                         |

### Option B: TinaCMS

| Aspect              | Detail                                                          |
|---------------------|-----------------------------------------------------------------|
| **Type**            | Git-based CMS with visual editing                              |
| **Differentiator**  | Live visual editing on your actual site                        |
| **Works With**      | Next.js (best), Astro, Hugo                                    |
| **Cost**            | Free for 2 users, $29/mo for teams                             |
| **Drawback**        | Deeper integration required; heavier setup                     |

### Option C: Keystatic

| Aspect              | Detail                                                          |
|---------------------|-----------------------------------------------------------------|
| **Type**            | Git-based CMS by the Thinkmill team                            |
| **Differentiator**  | First-class Astro/Next.js integration with reader API          |
| **Content Format**  | MDX, YAML, JSON — flexible schema                              |
| **Cost**            | Free (open source)                                             |
| **Drawback**        | Newer, smaller community                                       |

### Option D: Pure MDX (Developer-Only)

| Aspect              | Detail                                                          |
|---------------------|-----------------------------------------------------------------|
| **Type**            | Content as code — edit `.mdx` files directly in IDE or GitHub  |
| **Best For**        | Technical teams comfortable with Git                           |
| **Cost**            | $0                                                             |
| **Drawback**        | Non-developers can't contribute                                |

### Recommendation
1. **Start with pure MDX** — write all content as `.mdx` files in the repo
2. **Add Decap CMS later** — when non-developers need to edit, add the `/admin` interface
3. **Consider Keystatic** — if using Astro, its integration is excellent

---

## 11. Final Recommendation

### 🏆 Winner: Astro (Stack 3)

**Astro is the ideal stack for this project.** Here's why:

#### Why Astro Wins for This Specific Project

1. **Content-first architecture** — This is an informational site, not a web app. Astro is
   purpose-built for content sites. Next.js is built for web applications.

2. **Zero JS by default** — A luxury pool education site is 95% reading. Astro ships 0KB of
   JavaScript for static content. Next.js ships ~85KB+ of React runtime for the same content.

3. **Performance = Luxury feel** — Paradoxically, the fastest-loading site *feels* the most
   premium. Instant page loads + buttery GSAP animations = luxury.

4. **Perfect parent site integration** — Astro outputs plain HTML/CSS/JS. You can literally
   copy the parent site's CSS into Astro. With Next.js, you'd need to translate everything
   to Tailwind/CSS Modules.

5. **GSAP works natively** — Astro's `<script>` tags in `.astro` files work exactly like
   adding `<script>` to HTML. No React refs, no useEffect cleanup, no hydration issues.

6. **Content Collections with schema validation** — Type-safe MDX content with Zod schemas
   means your build fails if content is malformed. This catches errors before deployment.

7. **Progressive disclosure** — Use CSS-only details/summary for simple cases, React islands
   (`client:visible`) for complex interactive sections. Only the interactive parts ship JS.

8. **Easy learning curve** — `.astro` files look like HTML. A developer who knows the parent
   site's HTML/CSS can start writing Astro components immediately.

9. **Future-proof** — Need to add a quote calculator later? Drop in a React island. Need
   server rendering? Switch one config flag. Need a CMS? Add Decap or Keystatic.

#### Recommended Full Stack

```
Framework:    Astro 5.10+ (or 6.x)
Styling:      Tailwind CSS 4.x (design tokens matching parent site)
Content:      MDX via Astro Content Collections
Animations:   GSAP 3.14.x (ScrollTrigger + ScrollSmoother)
Smooth Scroll: Lenis 1.3.x
Simple FX:    AOS 2.3.4 (for basic reveals)
Islands:      @astrojs/react (only for interactive widgets)
Images:       Astro built-in <Image> + sharp
SEO:          @astrojs/sitemap + astro-seo
CMS (later):  Decap CMS (git-based, add when needed)
Hosting:      Cloudflare Pages (free, unlimited bandwidth)
```

#### Why NOT the Others

| Stack        | Why Not                                                              |
|--------------|----------------------------------------------------------------------|
| Static HTML  | Content management at 10+ sections is unmaintainable                |
| Next.js      | ~85KB React runtime for a content site is wasteful; overkill        |
| Hugo         | Go templates are painful; poor interactive feature support           |
| 11ty         | Good alternative but Astro's Content Collections + islands are better|

#### Migration Path from Existing Site

```
Phase 1: Set up Astro project with same CSS variables / design tokens
Phase 2: Create BaseLayout.astro matching existing HTML structure
Phase 3: Convert each section's content to MDX files
Phase 4: Add GSAP animations (copy existing JS, adapt to Astro)
Phase 5: Add progressive disclosure components
Phase 6: Deploy to Cloudflare Pages
Phase 7: (Optional) Add Decap CMS for non-dev content editing
```

---

## Appendix: Quick-Start Commands

### Astro Project Setup
```bash
# Create new Astro project
npm create astro@latest pool-education-site

# Add integrations
npx astro add tailwind mdx sitemap react

# Install animation libraries
npm install gsap lenis aos

# Install SEO
npm install astro-seo

# Development
npm run dev

# Build (static output)
npm run build

# Preview build locally
npm run preview
```

### Deploy to Cloudflare Pages
```bash
# In Cloudflare Dashboard:
# 1. Connect GitHub repository
# 2. Build command: npm run build
# 3. Output directory: dist
# 4. Done — auto-deploys on every push
```

---

*This evaluation was compiled July 2025 with the latest stable versions of all tools researched.*
