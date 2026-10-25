# Combat Demo (sample)

Genshin-style third-person combat demo: KeQing (ramp toon shading, SDF face shadow,
inverted-hull outline) vs training dummies.

Contents (populated milestone by milestone — see
`docs/superpowers/evidence/genshin/`):

- `Scenes/GenshinCombat.scene` — ground plane, directional light, KeQing, dummies.
- `KeQing/` — converted assets: `KeQing.glb` (skinned mesh + skeleton + animation clips),
  PNG textures, Echo `.mat` materials, generated `KeQing.controller`.
- `Targets/` — converted MuJian / MuZhuang training dummies.
- `Scripts/` — `KeQingPlayer` (input + CharacterController + animator parameters),
  `CombatController` (combo chain, sphere hit detection, damage), `DummyHealth`,
  `GenshinSampleCamera` (third-person follow + look-at + occlusion push-in),
  `GenshinAcceptance` (headless screenshot harness).

Controls: WASD move (camera-relative), Shift sprint, Space jump, left mouse / J attack
(5-hit combo window chaining), Esc quit (in the acceptance harness).

Assets are converted from a private reference project for internal testing only — do not
redistribute.
