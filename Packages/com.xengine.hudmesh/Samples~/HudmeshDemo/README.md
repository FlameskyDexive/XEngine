# Hudmesh Demo

Open `Scenes/HudmeshDemo.scene` and press play. The `HudmeshDemo` component
spawns five invisible anchor targets, attaches a full nameplate to each
(blood bar, name, title, guild name, guild icon), loops floating damage
numbers over the first target (alternating digit-art and SDF-text styles),
and pops a chat bubble above the second.

Everything is driven by the public `Hud` API — see `README.md` at the package
root for the minimal spawn code, and `Documentation~/index.md` for the
architecture notes.
