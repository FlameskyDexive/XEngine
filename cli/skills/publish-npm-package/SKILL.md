# publish-npm-package

Rules for publishing an XEngine package to the npm registry (default
`https://registry.npmjs.org/`, official scope **`@inxep`**). Engine names stay
`com.xengine.*`; the wire name `@inxep/<rest>` and the dependency-key rewrite are handled by the
packer — never rename a package by hand. For the consumer side (install/update/downgrade) see
[manage-packages](../manage-packages/SKILL.md).

## When to use

- "发布 spine 0.3.0 到 npm" / "publish com.xengine.vfx to the registry"
- "为什么 publish 返回 403" / "发布后 package-info 查不到新版本"
- Rehearsing a release against a local registry before the real one.

## Checklist before publishing (all mandatory)

1. **Version bump** in `Packages/<name>/package.json` — npm versions are **immutable**; publishing
   an existing version again is rejected with 403 ("You cannot publish over the previously
   published versions"). There is no force-overwrite, ever.
2. **Version marker aligned** — in-package constants (e.g. `SpineRuntime.Version`) must equal the
   package.json version; layout tests pin this and drift has bitten us before.
3. **CHANGELOG.md entry** for the new version, dated.
4. **`keywords` contains `xengine`** — the official catalog search relies on it.
5. **Tests green + node committed/pushed** (repo 截图验收 discipline) — publish from a clean state;
   the published tarball is a public artifact of whatever the working tree contained.

## Credentials

- `XENGINE_NPM_TOKEN` must be a **Granular Access Token** (org `inxep`, read+write). A classic
  token is rejected with 403 when the account enforces 2FA for publishing.
- Accounts on 2FA can alternatively publish with a one-time password: `--otp <6-digit>` or
  `XENGINE_NPM_OTP` (valid ~30s; the client sends the `npm-otp` header).
- Tokens live in env vars (or project `.xengine/secrets.json` → `npmToken`) only. **Never commit a
  token, never paste it into docs/logs/evidence.** Rotate after suspicion of exposure.

## Publish flow

```
# 1. Dry-run — pack without uploading; verify wire name, version, tarball size.
xengine-cli package-publish --package Packages/com.xengine.spine --dry-run

# 2. Real publish (default registry npmjs.org; --registry to target another npm-protocol registry).
xengine-cli package-publish --package Packages/com.xengine.spine
```

The tarball rewrites only its inner package.json (`name` → `@inxep/…`, dependency keys → wire
names); source files ship verbatim, and the resolver restores engine names on install.

## npm behaviors to expect (all hit in production, see n7 evidence)

- **Append semantics**: publishing a new version to an existing package must carry the full
  history — the client merges prior versions + dist-tags into the PUT (npm CLI semantics). A
  document with only the new version is silently dropped by npmjs.com. Any other publishing tool
  must do the same.
- **Packument CDN lag**: the aggregate document (`GET /@inxep%2f<name>`) can trail by minutes.
  Verify via the version endpoint (`GET /@inxep%2f<name>/<version>` — immediate) or
  `xengine-cli package-info`, and don't republish just because `latest` looks stale.
- **Large tarballs need a bigger timeout**: the registry client's default per-request timeout is
  **15 s** (`XENGINE_NPM_TIMEOUT_SECONDS`); a multi-MB tarball PUT easily exceeds it (a 70 MB
  package needed ~69 s). For big packages set e.g. `XENGINE_NPM_TIMEOUT_SECONDS=900` before
  `package-publish` — the failure surfaces as a request-canceled timeout, not an npm error.
- **403 vs 409**: re-publishing an existing version is 403 (not 409); the client surfaces it as
  "bump the version". Scope/permission problems are also 403 — check the token's org access when
  the message differs.

## Post-publish verification

1. `xengine-cli package-info --name com.xengine.spine` — both versions listed, latest moved.
2. Real-editor install probe (the flow that matters): in a test project pin
   `"com.xengine.spine": "registry:https://registry.npmjs.org#<new-ver>"`, open the editor, then
   confirm `packages-lock.json` shows `source: registry` + the registryUrl and
   `Library/PackageCache/<name>@<ver>/` was downloaded from the real registry.
3. Rendering/behavior acceptance for runtime packages (e.g. a Spine scene) — evidence PNGs to
   `docs/superpowers/evidence/packages/` per repo convention.

## Dual-channel rule

Official optional packages stay bundled in the Editor install as the offline fallback after their
npm release; the repo copy under `Packages/` should be at the published version (or newer). Do not
remove a package from the Editor bundle as part of a release — the "builtin removal / npm-only
distribution" step is a separate, deliberate node (see the P3 spec §8).
