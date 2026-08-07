# REFERENCE MATERIAL

## Token architecture

Two tiers plus scalars. **Hard rule: any `.svx-*` component rule that names a literal colour, or references a Tier-1 primitive, is a defect.** Only `currentColor` and `transparent` are exempt. Component CSS may use Tier-2 semantic tokens only.

Accent is **Signal Cyan** (cyan-700 in light, cyan-400 in dark): ~90 degrees off the removed blurple so there is no family resemblance; green/amber/red are reserved for healthy/degraded/crashed; cyan reads technical rather than decorative and has contrast headroom in both directions. There is no separate blue "info" hue — info tokens alias the accent and transitional states are differentiated by a pulsing dot, so the distinction is never colour-only. `--svx-info-*` exist as real names pointing at accent values, so re-pointing them later changes zero call sites.

### Tier 1 — primitives (`:root`, theme-invariant)

```css
:root {
  /* Neutral ramp "Graphite" — cool-tinted, low chroma */
  --svx-gray-0:    #FFFFFF;
  --svx-gray-25:   #FAFBFC;
  --svx-gray-50:   #F4F6F9;
  --svx-gray-100:  #EBEEF3;
  --svx-gray-200:  #DEE3EB;
  --svx-gray-300:  #C6CDD9;
  --svx-gray-400:  #A3ACBB;
  --svx-gray-500:  #808B9C;
  --svx-gray-600:  #647082;
  --svx-gray-700:  #4A5566;
  --svx-gray-800:  #343D4C;
  --svx-gray-850:  #262E3A;
  --svx-gray-900:  #1A2029;
  --svx-gray-925:  #141920;
  --svx-gray-950:  #0F1319;
  --svx-gray-975:  #0A0D12;
  --svx-gray-1000: #06080B;

  /* Accent ramp "Signal Cyan" */
  --svx-cyan-50:   #ECFEFF;
  --svx-cyan-100:  #CFFAFE;
  --svx-cyan-200:  #A5F3FC;
  --svx-cyan-300:  #67E8F9;
  --svx-cyan-400:  #22D3EE;
  --svx-cyan-500:  #06B6D4;
  --svx-cyan-600:  #0891B2;
  --svx-cyan-700:  #0E7490;
  --svx-cyan-800:  #155E75;
  --svx-cyan-900:  #164E63;
  --svx-cyan-950:  #083344;
  --svx-cyan-1000: #04202B;

  /* Success / healthy / running */
  --svx-emerald-50:  #ECFDF5;
  --svx-emerald-100: #D1FAE5;
  --svx-emerald-200: #A7F3D0;
  --svx-emerald-300: #6EE7B7;
  --svx-emerald-400: #34D399;
  --svx-emerald-500: #10B981;
  --svx-emerald-600: #059669;
  --svx-emerald-700: #047857;
  --svx-emerald-800: #065F46;
  --svx-emerald-900: #064E3B;
  --svx-emerald-950: #052E22;

  /* Warning / degraded / read-only */
  --svx-amber-50:  #FFFBEB;
  --svx-amber-100: #FEF3C7;
  --svx-amber-200: #FDE68A;
  --svx-amber-300: #FCD34D;
  --svx-amber-400: #FBBF24;
  --svx-amber-500: #F59E0B;
  --svx-amber-600: #D97706;
  --svx-amber-700: #B45309;
  --svx-amber-800: #92400E;
  --svx-amber-900: #78350F;
  --svx-amber-950: #3A2405;

  /* Danger / crashed / drift / destructive */
  --svx-red-50:  #FEF2F2;
  --svx-red-100: #FEE2E2;
  --svx-red-200: #FECACA;
  --svx-red-300: #FCA5A5;
  --svx-red-400: #F87171;
  --svx-red-500: #EF4444;
  --svx-red-600: #DC2626;
  --svx-red-700: #B91C1C;
  --svx-red-800: #991B1B;
  --svx-red-900: #7F1D1D;
  --svx-red-950: #3A0F12;
}
```

### Tier 2b — scalars (`:root`, theme-invariant)

```css
:root {
  --svx-space-0:  0;
  --svx-space-1:  0.25rem;
  --svx-space-2:  0.5rem;
  --svx-space-3:  0.75rem;
  --svx-space-4:  1rem;
  --svx-space-5:  1.25rem;
  --svx-space-6:  1.5rem;
  --svx-space-8:  2rem;
  --svx-space-10: 2.5rem;
  --svx-space-12: 3rem;
  --svx-space-16: 4rem;
  --svx-space-20: 5rem;

  --svx-radius-xs:   0.25rem;
  --svx-radius-sm:   0.375rem;
  --svx-radius-md:   0.5rem;
  --svx-radius-lg:   0.75rem;
  --svx-radius-xl:   1rem;
  --svx-radius-2xl:  1.25rem;
  --svx-radius-full: 9999px;

  --svx-font-sans: ui-sans-serif, system-ui, -apple-system, "Segoe UI Variable Text",
                   "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
  --svx-font-mono: ui-monospace, "Cascadia Code", "JetBrains Mono", SFMono-Regular,
                   Consolas, "Liberation Mono", monospace;

  --svx-text-2xs: 0.6875rem;
  --svx-text-xs:  0.75rem;
  --svx-text-sm:  0.8125rem;
  --svx-text-base:0.875rem;
  --svx-text-md:  1rem;
  --svx-text-lg:  1.125rem;
  --svx-text-xl:  1.375rem;
  --svx-text-2xl: 1.75rem;

  --svx-leading-tight:  1.25;
  --svx-leading-normal: 1.5;
  --svx-leading-relaxed:1.65;

  --svx-tracking-wide:  0.02em;
  --svx-tracking-caps:  0.06em;

  --svx-weight-normal:   400;
  --svx-weight-medium:   500;
  --svx-weight-semibold: 600;
  --svx-weight-bold:     700;

  --svx-sidebar-width:      248px;
  --svx-sidebar-width-rail: 68px;
  --svx-topbar-height:      3.5rem;

  --svx-z-content:  0;
  --svx-z-topbar:   10;
  --svx-z-scrim:    15;
  --svx-z-sidebar:  20;
  --svx-z-popover:  50;
  --svx-z-error-ui: 1000;

  --svx-duration-fast: 120ms;
  --svx-duration-base: 180ms;
  --svx-duration-slow: 280ms;
  --svx-ease: cubic-bezier(0.2, 0, 0.15, 1);
}
```

### Tier 2 — semantics, LIGHT

```css
:root,
:root[data-theme="light"] {
  color-scheme: light;

  --svx-bg-sunken:   #E8ECF2;
  --svx-bg-base:     var(--svx-gray-50);
  --svx-bg-subtle:   var(--svx-gray-100);
  --svx-bg-surface:  var(--svx-gray-0);
  --svx-bg-raised:   var(--svx-gray-0);
  --svx-bg-hover:    var(--svx-gray-100);
  --svx-bg-active:   var(--svx-gray-200);
  --svx-bg-overlay:  rgba(15, 19, 25, 0.45);
  --svx-bg-inset:    var(--svx-gray-100);

  --svx-sidebar-bg:            #E8ECF2;
  --svx-sidebar-border:        var(--svx-gray-200);
  --svx-sidebar-text:          var(--svx-gray-700);
  --svx-sidebar-text-active:   var(--svx-gray-900);
  --svx-sidebar-item-hover:    rgba(26, 32, 41, 0.06);
  --svx-sidebar-item-active:   var(--svx-gray-0);
  --svx-sidebar-brand-text:    var(--svx-gray-900);

  --svx-border-subtle:  var(--svx-gray-100);
  --svx-border-default: var(--svx-gray-200);
  --svx-border-strong:  var(--svx-gray-500);
  --svx-border-inverse: rgba(255, 255, 255, 0.14);

  --svx-text-primary:  var(--svx-gray-900);
  --svx-text-muted:    var(--svx-gray-600);
  --svx-text-subtle:   var(--svx-gray-500);
  --svx-text-inverted: var(--svx-gray-0);
  --svx-text-disabled: var(--svx-gray-500);
  --svx-text-link:     var(--svx-cyan-700);

  --svx-accent:               var(--svx-cyan-700);
  --svx-accent-hover:         var(--svx-cyan-800);
  --svx-accent-active:        var(--svx-cyan-900);
  --svx-accent-fg:            #FFFFFF;
  --svx-accent-subtle:        var(--svx-cyan-50);
  --svx-accent-subtle-border: var(--svx-cyan-200);
  --svx-accent-on-subtle:     var(--svx-cyan-800);
  --svx-accent-glow:          rgba(14, 116, 144, 0.18);

  --svx-info:            var(--svx-accent);
  --svx-info-bg:         var(--svx-accent-subtle);
  --svx-info-border:     var(--svx-accent-subtle-border);
  --svx-info-fg:         var(--svx-accent-on-subtle);

  --svx-success:        var(--svx-emerald-600);
  --svx-success-bg:     var(--svx-emerald-50);
  --svx-success-border: var(--svx-emerald-200);
  --svx-success-fg:     var(--svx-emerald-700);
  --svx-success-solid:  var(--svx-emerald-500);

  --svx-warning:        var(--svx-amber-600);
  --svx-warning-bg:     var(--svx-amber-50);
  --svx-warning-border: var(--svx-amber-200);
  --svx-warning-fg:     var(--svx-amber-700);
  --svx-warning-solid:  var(--svx-amber-500);

  --svx-danger:         var(--svx-red-600);
  --svx-danger-bg:      var(--svx-red-50);
  --svx-danger-border:  var(--svx-red-200);
  --svx-danger-fg:      var(--svx-red-700);
  --svx-danger-solid:   var(--svx-red-500);
  --svx-danger-strong:  var(--svx-red-600);
  --svx-danger-wash:    rgba(220, 38, 38, 0.07);

  --svx-neutral-bg:     var(--svx-gray-100);
  --svx-neutral-border: var(--svx-gray-200);
  --svx-neutral-fg:     var(--svx-gray-600);
  --svx-neutral-solid:  var(--svx-gray-400);

  --svx-live-dot:   var(--svx-emerald-500);
  --svx-live-halo:  rgba(16, 185, 129, 0.20);
  --svx-live-glow:  0 0 0 3px rgba(16, 185, 129, 0.16);

  --svx-focus-ring:        var(--svx-cyan-700);
  --svx-focus-ring-inner:  #FFFFFF;
  --svx-focus-ring-width:  2px;
  --svx-focus-ring-offset: 2px;

  --svx-console-bg:      var(--svx-gray-1000);
  --svx-console-fg:      #D5DBE6;
  --svx-console-dim:     #7A8496;
  --svx-console-warn:    var(--svx-amber-300);
  --svx-console-err:     var(--svx-red-400);
  --svx-console-border:  rgba(255, 255, 255, 0.10);

  --svx-skeleton-base:  var(--svx-gray-200);
  --svx-skeleton-sheen: rgba(255, 255, 255, 0.65);

  --svx-shadow-xs: 0 1px 2px 0 rgba(16, 24, 40, 0.05);
  --svx-shadow-sm: 0 1px 3px 0 rgba(16, 24, 40, 0.08),
                   0 1px 2px -1px rgba(16, 24, 40, 0.06);
  --svx-shadow-md: 0 4px 8px -2px rgba(16, 24, 40, 0.10),
                   0 2px 4px -2px rgba(16, 24, 40, 0.06);
  --svx-shadow-lg: 0 12px 16px -4px rgba(16, 24, 40, 0.10),
                   0 4px 6px -2px rgba(16, 24, 40, 0.04);
  --svx-shadow-xl: 0 20px 24px -4px rgba(16, 24, 40, 0.12),
                   0 8px 8px -4px rgba(16, 24, 40, 0.04);

  /* Legacy aliases — kept permanently so existing call sites keep working. */
  --svx-bg:         var(--svx-bg-base);
  --svx-surface:    var(--svx-bg-surface);
  --svx-border:     var(--svx-border-default);
  --svx-text:       var(--svx-text-primary);
  --svx-hover:      var(--svx-bg-hover);
}
```

### Tier 2 — semantics, DARK

```css
:root[data-theme="dark"] {
  color-scheme: dark;

  --svx-bg-sunken:   var(--svx-gray-1000);
  --svx-bg-base:     var(--svx-gray-975);
  --svx-bg-subtle:   var(--svx-gray-950);
  --svx-bg-surface:  var(--svx-gray-925);
  --svx-bg-raised:   var(--svx-gray-900);
  --svx-bg-hover:    var(--svx-gray-900);
  --svx-bg-active:   var(--svx-gray-850);
  --svx-bg-overlay:  rgba(3, 5, 8, 0.72);
  --svx-bg-inset:    var(--svx-gray-1000);

  --svx-sidebar-bg:          var(--svx-gray-1000);
  --svx-sidebar-border:      rgba(255, 255, 255, 0.07);
  --svx-sidebar-text:        #99A3B3;
  --svx-sidebar-text-active: #E6EAF0;
  --svx-sidebar-item-hover:  rgba(255, 255, 255, 0.055);
  --svx-sidebar-item-active: rgba(34, 211, 238, 0.10);
  --svx-sidebar-brand-text:  #E6EAF0;

  --svx-border-subtle:  rgba(255, 255, 255, 0.055);
  --svx-border-default: rgba(255, 255, 255, 0.10);
  --svx-border-strong:  rgba(255, 255, 255, 0.22);
  --svx-border-inverse: rgba(0, 0, 0, 0.22);

  --svx-text-primary:  #E6EAF0;
  --svx-text-muted:    #99A3B3;
  --svx-text-subtle:   #78849A;
  --svx-text-inverted: var(--svx-gray-975);
  --svx-text-disabled: #66717F;
  --svx-text-link:     var(--svx-cyan-400);

  --svx-accent:               var(--svx-cyan-400);
  --svx-accent-hover:         var(--svx-cyan-300);
  --svx-accent-active:        var(--svx-cyan-200);
  --svx-accent-fg:            var(--svx-cyan-1000);
  --svx-accent-subtle:        #0A2A34;
  --svx-accent-subtle-border: #16505F;
  --svx-accent-on-subtle:     var(--svx-cyan-300);
  --svx-accent-glow:          rgba(34, 211, 238, 0.30);

  --svx-info:        var(--svx-accent);
  --svx-info-bg:     var(--svx-accent-subtle);
  --svx-info-border: var(--svx-accent-subtle-border);
  --svx-info-fg:     var(--svx-accent-on-subtle);

  --svx-success:        var(--svx-emerald-400);
  --svx-success-bg:     var(--svx-emerald-950);
  --svx-success-border: #11603F;
  --svx-success-fg:     var(--svx-emerald-400);
  --svx-success-solid:  var(--svx-emerald-400);

  --svx-warning:        var(--svx-amber-400);
  --svx-warning-bg:     var(--svx-amber-950);
  --svx-warning-border: #7A4E10;
  --svx-warning-fg:     var(--svx-amber-400);
  --svx-warning-solid:  var(--svx-amber-400);

  --svx-danger:        var(--svx-red-400);
  --svx-danger-bg:     var(--svx-red-950);
  --svx-danger-border: #7F2326;
  --svx-danger-fg:     var(--svx-red-400);
  --svx-danger-solid:  var(--svx-red-500);
  --svx-danger-strong: var(--svx-red-400);
  --svx-danger-wash:   rgba(248, 113, 113, 0.11);

  --svx-neutral-bg:     var(--svx-gray-900);
  --svx-neutral-border: rgba(255, 255, 255, 0.10);
  --svx-neutral-fg:     #99A3B3;
  --svx-neutral-solid:  var(--svx-gray-500);

  --svx-live-dot:  var(--svx-emerald-400);
  --svx-live-halo: rgba(52, 211, 153, 0.26);
  --svx-live-glow: 0 0 0 3px rgba(52, 211, 153, 0.18),
                   0 0 10px 0 rgba(52, 211, 153, 0.38);

  --svx-focus-ring:       var(--svx-cyan-400);
  --svx-focus-ring-inner: var(--svx-gray-1000);

  --svx-console-bg:     var(--svx-gray-1000);
  --svx-console-fg:     #D5DBE6;
  --svx-console-dim:    #7A8496;
  --svx-console-warn:   var(--svx-amber-300);
  --svx-console-err:    var(--svx-red-400);
  --svx-console-border: rgba(255, 255, 255, 0.08);

  --svx-skeleton-base:  var(--svx-gray-850);
  --svx-skeleton-sheen: rgba(255, 255, 255, 0.07);

  --svx-shadow-xs: 0 1px 2px 0 rgba(0, 0, 0, 0.40);
  --svx-shadow-sm: 0 1px 3px 0 rgba(0, 0, 0, 0.50),
                   0 1px 2px -1px rgba(0, 0, 0, 0.40);
  --svx-shadow-md: 0 4px 10px -2px rgba(0, 0, 0, 0.55),
                   inset 0 1px 0 0 rgba(255, 255, 255, 0.04);
  --svx-shadow-lg: 0 14px 28px -6px rgba(0, 0, 0, 0.65),
                   inset 0 1px 0 0 rgba(255, 255, 255, 0.05);
  --svx-shadow-xl: 0 24px 48px -12px rgba(0, 0, 0, 0.75),
                   inset 0 1px 0 0 rgba(255, 255, 255, 0.06);

  --svx-bg:      var(--svx-bg-base);
  --svx-surface: var(--svx-bg-surface);
  --svx-border:  var(--svx-border-default);
  --svx-text:    var(--svx-text-primary);
  --svx-hover:   var(--svx-bg-hover);
}
```

### svx-reset (replaces Bootstrap Reboot)

```css
*, *::before, *::after { box-sizing: border-box; }

html { -webkit-text-size-adjust: 100%; }

body {
    margin: 0;
    min-height: 100vh;
    font-family: var(--svx-font-sans);
    font-size: var(--svx-text-base);
    line-height: var(--svx-leading-normal);
    color: var(--svx-text-primary);
    background-color: var(--svx-bg-base);
    -webkit-font-smoothing: antialiased;
    -moz-osx-font-smoothing: grayscale;
}

h1, h2, h3, h4, h5, h6 {
    margin: 0 0 var(--svx-space-2) 0;
    font-weight: var(--svx-weight-semibold);
    line-height: var(--svx-leading-tight);
    color: var(--svx-text-primary);
}

p { margin: 0 0 var(--svx-space-3) 0; }
p:last-child { margin-bottom: 0; }

a { color: var(--svx-text-link); text-decoration: none; }
a:hover { text-decoration: underline; }

ul, ol { margin: 0 0 var(--svx-space-3) 0; padding-left: var(--svx-space-5); }

button, input, select, textarea { font: inherit; color: inherit; margin: 0; }
button { cursor: pointer; }
button:disabled, input:disabled, select:disabled, textarea:disabled { cursor: not-allowed; }

fieldset { min-width: 0; padding: 0; margin: 0; border: 0; }
legend { padding: 0; }

img, svg, video { max-width: 100%; display: block; }
svg { flex: 0 0 auto; }

table { border-collapse: collapse; width: 100%; }

hr { border: 0; border-top: 1px solid var(--svx-border-subtle); margin: var(--svx-space-4) 0; }

code, kbd, pre, samp { font-family: var(--svx-font-mono); font-size: 0.9em; }

dl, dd { margin: 0; }

::selection { background: var(--svx-accent-subtle); color: var(--svx-accent-on-subtle); }

::-webkit-scrollbar { width: 10px; height: 10px; }
::-webkit-scrollbar-track { background: transparent; }
::-webkit-scrollbar-thumb {
    background: var(--svx-border-strong);
    border: 2px solid transparent;
    background-clip: content-box;
    border-radius: var(--svx-radius-full);
}
::-webkit-scrollbar-thumb:hover { background-color: var(--svx-text-subtle); background-clip: content-box; }

:focus { outline: none; }
:focus-visible {
    outline: var(--svx-focus-ring-width) solid var(--svx-focus-ring);
    outline-offset: var(--svx-focus-ring-offset);
    border-radius: var(--svx-radius-xs);
}

@media (prefers-reduced-motion: reduce) {
    *, *::before, *::after {
        animation-duration: 0.01ms !important;
        animation-iteration-count: 1 !important;
        transition-duration: 0.01ms !important;
        scroll-behavior: auto !important;
    }
}
```

## Theme switching contract

Two attributes on `<html>`:
- `data-theme` = the **resolved** theme, `light` or `dark`. Never `system`. All CSS keys off this.
- `data-theme-choice` = the operator's **stored preference**, `system` | `light` | `dark`. Only the toggle UI keys off this.

localStorage key is **`svx-theme`**; stored values are exactly `light`, `dark`, or `system`. An explicitly stored `light`/`dark` always wins over `prefers-color-scheme`. There is deliberately **no `@media (prefers-color-scheme)` rule in the CSS** — the bootstrap script resolves the OS preference into `data-theme`, giving exactly one source of truth.

Blazor Server prerender agreement: the server never renders `data-theme`; no component renders theme-dependent markup, so the prerendered DOM and the post-hydration DOM are identical and there is no hydration mismatch. `<html>` is outside the Blazor render tree, so the script's mutation is permanent. Never call `IJSRuntime` before `OnAfterRenderAsync(firstRender: true)` — prerender has no JS.

### Inline no-flash bootstrap script

Must appear byte-identical in BOTH `Components/App.razor` and `Components/Pages/Login/LoginPage.razor` (`/login` is a `RazorComponentResult<LoginPage>` served from a minimal-API endpoint and never passes through `App.razor`).

```html
<script>
    // Servyx theme bootstrap. Runs before first paint so the resolved theme is already
    // on <html> when the stylesheets apply — no light-to-dark flash on load.
    // DUPLICATED VERBATIM in Components/App.razor and Components/Pages/Login/LoginPage.razor.
    // Keep the two copies identical. Full API lives in wwwroot/js/servyx-theme.js.
    (function () {
        var e = document.documentElement;
        try {
            var c = localStorage.getItem('svx-theme');
            if (c !== 'light' && c !== 'dark') { c = 'system'; }
            var r = c === 'system'
                ? (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
                : c;
            e.setAttribute('data-theme', r);
            e.setAttribute('data-theme-choice', c);
            e.style.colorScheme = r;
        } catch (_) {
            e.setAttribute('data-theme', 'light');
            e.setAttribute('data-theme-choice', 'system');
        }
    })();
</script>
```

### wwwroot/js/servyx-theme.js

```js
// Theme persistence and resolution. Loaded normally (non-blocking) — the *before-paint*
// work is done by the inline bootstrap in App.razor / LoginPage.razor, which deliberately
// duplicates the small resolve step so it needs no network fetch.
window.servyxTheme = {
    storageKey: 'svx-theme',

    /** 'system' | 'light' | 'dark' — whatever the operator last chose. */
    read: function () {
        try {
            var c = localStorage.getItem(window.servyxTheme.storageKey);
            return (c === 'light' || c === 'dark') ? c : 'system';
        } catch (_) {
            return 'system';
        }
    },

    /** Collapses a choice to a concrete 'light' | 'dark'. */
    resolve: function (choice) {
        if (choice === 'light' || choice === 'dark') { return choice; }
        return (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches)
            ? 'dark' : 'light';
    },

    /** Writes both attributes on <html>. Returns the resolved theme. */
    apply: function (choice) {
        var c = (choice === 'light' || choice === 'dark' || choice === 'system')
            ? choice
            : window.servyxTheme.read();
        var r = window.servyxTheme.resolve(c);
        var e = document.documentElement;
        e.setAttribute('data-theme', r);
        e.setAttribute('data-theme-choice', c);
        e.style.colorScheme = r;
        return r;
    },

    /** Persists then applies. Returns the resolved theme. */
    set: function (choice) {
        try { localStorage.setItem(window.servyxTheme.storageKey, choice); } catch (_) { }
        return window.servyxTheme.apply(choice);
    },
};

// While the choice is 'system', follow the OS live.
if (window.matchMedia) {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
        if (window.servyxTheme.read() === 'system') { window.servyxTheme.apply('system'); }
    });
}

// Enhanced navigation swaps <head>; <html> attributes survive, but re-asserting is free.
document.addEventListener('enhancedload', function () { window.servyxTheme.apply(); });
```

## Cross-cutting invariants (every phase, every agent)

1. **No literal colour outside `wwwroot/theme.css`.** Check: `rg "#[0-9a-fA-F]{3,8}|rgba?\(" --glob "*.css" --glob "!**/lib/**" --glob "!**/theme.css" src/Presentation/Servyx.Web`
2. **No `var(--svx-*, fallback)`.** Check: `rg "var\(--svx-[a-z0-9-]+\s*,"`. Fallbacks silently defeat dark mode and hide undeclared-token bugs.
3. **No consumed-but-undeclared tokens.** Sweep: `rg -o --no-filename "var\(--svx-[a-z0-9-]+" src/Presentation/Servyx.Web | sed 's/var(//' | sort -u`, diff against the declarations in `theme.css`.
4. **No `opacity` used to de-emphasise text.** Use `--svx-text-muted` / `--svx-text-subtle`. Opacity over an unknown backdrop is not contrast-predictable.
5. **The two inline bootstrap scripts stay byte-identical.**
6. **Never call `IJSRuntime` before `OnAfterRenderAsync(firstRender: true)`.**

## Known pre-existing defects this work must fix

- **F1** `DeployPage.razor.css` and `BackupsPage.razor.css` were authored against a dark canvas (`rgba(255,255,255,0.18)` borders, `rgba(255,255,255,0.04)` fills) but render on a light page — inputs currently have invisible borders.
- **F2** `app.css:644-645` `.svx-data-impact` has `rgba(255,255,255,0.14)` border / `rgba(255,255,255,0.03)` background — invisible on white, so the component's "unmissable" contract is honoured in dark only by accident.
- **F3** `.svx-cost-confidence` (`CostEstimateView.razor:9`) is never declared — styled entirely by Bootstrap `.badge`.
- **F4** `.svx-drift-badge` (`DeployPage.razor.css:130-138`) sets `border-color` but never `border-style`/`border-width`, so the border never renders.
- **F5** `tests/Presentation/Servyx.Web.Tests/Pages/BackupsPageTests.cs:127` uses `cut.FindAll(".badge")` — the only Bootstrap-selector coupling in the test suite.
- `--svx-hover` was consumed at `MainLayout.razor.css:58` but never declared anywhere (now declared).
- `MainLayout.razor.css:169` pins `color-scheme: light only` on `#blazor-error-ui` — must be deleted.

## Bootstrap removal

Bootstrap 5 is unlinked from `App.razor`. Full grep found zero usage of `container`/`row`/`col-*`/`d-flex`/`card`/`alert`/`table`/`navbar`/`modal`/`list-group`/utility classes. The entire real surface is five class tokens across 40 call sites: `btn` (17), `btn-primary` (9), `btn-secondary` (1), `badge` (14), `form-control`/`form-control-sm` (1). Replacements `.svx-btn*`, `.svx-badge`, `.svx-input*` are defined in `app.css` by Phase 2. The vendored `wwwroot/lib/bootstrap/` directory is deleted in a separate final commit so a rollback is one line.

Reason for removal rather than scoping: Bootstrap's reboot sets `body { color: #212529; background-color: #fff; }` on every page, which defeats dark mode. Bootstrap 5.3's fix requires adopting `data-bs-theme` as a second theme attribute kept in sync, or overriding ~40 `--bs-*` variables in both directions — both strictly more work than deleting 41 lines of markup and shipping a 60-line reset.

## Build phases

- **Phase 1 (gate):** `wwwroot/theme.css` (new), `wwwroot/js/servyx-theme.js` (new), `Components/App.razor`.
- **Phase 2:** `wwwroot/app.css` exclusively — delete lines 1-60, 66-72, 74-77; retokenize everything; append `.svx-btn*`, `.svx-input*`, `.svx-badge`, `.svx-dot*`, `.svx-visually-hidden`.
- **Phase 3:** `MainLayout.razor{,.css}`, `NavMenu.razor.css`, `ReconnectModal.razor.css`, new `Components/Shared/ThemeToggle.razor{,.css}`.
- **Phase 4:** `Components/Pages/Login/LoginPage.razor` exclusively. Fully parallel with 2 and 3.
- **Phase 5a:** `Deploy/DeployPage.razor{,.css}`. **Phase 5b:** `Backups/BackupsPage.razor{,.css}` + `BackupsPageTests.cs`.
- **Phase 6:** remaining `Components/Shared/*`, `Home.razor`, `GamesPage.razor`, `Servers/*.razor`; new `Skeleton.razor{,.css}`.
- **Phase 7:** delete `wwwroot/lib/bootstrap/`, regenerate screenshots, docs.

Phases 2, 3, 4 have zero file overlap and run concurrently. Empty states are deliberately NOT extracted into a shared component during this work — all 15 inline implementations already share the `.svx-empty-state` class, so one CSS rule reaches them all; extraction is filed as separate follow-up work.
