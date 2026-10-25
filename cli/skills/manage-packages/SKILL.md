# manage-packages

Search, install, update, downgrade, and publish XEngine packages through the npm-protocol registry.
Engine names are `com.xengine.*`; the registry wire name is the `@inxep` scope (`@inxep/spine`) —
the tools map between the two automatically, so always use the `com.xengine.*` form.

## When to use

- "Install spine into this project" / "add the cinemachine package"
- "What packages are available / what does com.xengine.spine's latest version look like?"
- "Update all packages" / "roll spine back to 0.1.1"
- "Publish this package directory to the registry" (engine-dev / package authors)

## Registry configuration

Default registry is `https://registry.npmjs.org/` (`@inxep` scope). Override per machine with
`XENGINE_NPM_REGISTRY` (e.g. a private Verdaccio, or `http://127.0.0.1:4873/` against a local
`xengine-fake-npm` rehearsal host). Publishing needs `XENGINE_NPM_TOKEN` (or
`.xengine/secrets.json` → `"npmToken"`). `XENGINE_NPM_DISABLE=1` forces offline resolution from
`Library/PackageCache` / the lock file.

## Read before write

1. `package_search` (MCP) or `xengine-cli package-search --query <text>` — discover packages.
   npm qualifiers work (`scope:xengine`, `keywords:spine`).
2. `package_versions` / `package-info --name com.xengine.spine` — all published versions, latest
   dist-tag, and per-version dependencies (engine names).
3. `package_list` — what the project currently declares.

## Install / update / downgrade

Mutation writes `Packages/manifest.json` only; the editor's manifest watcher re-resolves and
downloads into `Library/PackageCache` (verified + locked). Prefer MCP `package_add` for a known
specifier, or the CLI verbs for version lookup + write in one step:

```
xengine-cli package-install --name com.xengine.spine                 # latest
xengine-cli package-install --name com.xengine.spine --version 0.1.1 # downgrade/pin
xengine-cli package-update --all                                     # bump bare version pins
```

Specifier forms: bare version `"0.2.0"` (resolves embed → built-in if the bundled version satisfies
→ scoped registry → implicit official npm registry), `registry:<url>#<ver>` for an explicit
registry, plus the pre-existing `embed:` / `file:` / `git@…` sources. After editing, the editor
resolves on next open; upgrades/downgrades are transactional (manifest+lock roll back on failure)
and prune superseded `Library/PackageCache/<name>@<ver>` entries.

## Publish (package authors)

Publishing rules — version/marker/changelog/keywords checklist, granular-token or OTP credentials,
dry-run, npm append semantics, CDN lag, and post-publish verification — live in the dedicated
[publish-npm-package](../publish-npm-package/SKILL.md) skill. Quick form:

```
xengine-cli package-publish --package Packages/com.xengine.spine --dry-run  # pack + report, no upload
xengine-cli package-publish --package Packages/com.xengine.spine           # PUT to the registry
```

