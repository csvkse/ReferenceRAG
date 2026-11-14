# Local browser dependencies

- vue 3.5.32 (MIT); copied from the installed browser distribution.
- naive-ui 2.44.1 (MIT); copied from the installed browser distribution.
- vue-router 4.6.4 (MIT); copied from the installed browser distribution.
- axios 1.14.0 (MIT); copied from the installed browser distribution.
- marked 18.0.4 (MIT); copied from the installed browser distribution.
- @microsoft/signalr 8.0.17 (MIT); copied from the installed browser distribution.
- @vicons/ionicons5 0.12.0 (MIT); copied from the installed browser distribution.

No CDN or frontend build is used at runtime or publish. Licenses are in licenses/.

## Cache versioning (P2-1)

The backend serves `/vendor/*` with `Cache-Control: public,max-age=31536000,immutable`
(one-year strong cache). Every vendor URL in `index.html` (the importmap entries and the
classic `signalr.js` script) therefore carries a `?v=<n>` query. App code (`app.js`,
`core/`, `app/`, `styles/`, `index.html`) stays `no-cache` so releases take effect immediately.

**Bump rule:** whenever any file under `vendor/` changes (library upgrade, icon added,
patch), increment the single integer `?v=` on *all* vendor URLs in `index.html`
(e.g. `?v=1` → `?v=2`). The version is a literal in `index.html` because the importmap is
static JSON parsed before any module loads (no build step). `check.mjs` strips the query
when resolving modules; `scripts/check-perf.mjs` (G-M8) asserts every vendor URL carries a
consistent `?v=` query. `pinia` maps to app code (`/core/state.js`) and is intentionally unversioned.

**Importmap-only rule:** app code must reference vendor libraries exclusively through the
bare importmap specifiers (`vue`, `vue-router`, `naive-ui`, `axios`, `marked`,
`@vicons/ionicons5`, `@microsoft/signalr`). Relative/absolute path imports of `vendor/*.js`
are forbidden: under `immutable` strong caching an unversioned URL would keep serving stale
content after a `?v=` bump. `scripts/check-perf.mjs` (G-M8) statically enforces this rule.
Node unit tests resolve the same bare specifiers through `tests/importmap-loader.mjs`
(a `node:module` resolve hook reading the importmap), keeping browser and test resolution
single-sourced.
