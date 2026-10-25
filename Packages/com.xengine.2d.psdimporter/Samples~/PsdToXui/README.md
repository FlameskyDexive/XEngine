# PSD → XUI sample

One layered Photoshop document, one command, a working screen.

## What's in here

| Path | What it is |
|---|---|
| `Art/login-demo.psd` | A 400×240 demo document exercising the whole naming contract: a `btn_login=Login` group (blue bg + `txt_label` child that becomes the inline label), `txt_title=Account Login`, `input_user=Enter username`, `img_avatar`, a `panel` group with a `cell` bar, and a full-bleed `bg`. |
| `UI/logindemo/LoginDemo.xui` + `UI/logindemo/sprites/` | The converter's output: the screen document and the per-layer sprite PNGs with sprite-mode-Single metas (GUIDs stable across re-runs). |
| `UI/logindemo/LoginDemoView.g.cs`, `UI/logindemo/LoginDemo.cs` | `xui-scaffold` output plus the filled-in controller: click `Login` → username validation → `Xui.Toast` + title feedback. |

Requires both `com.xengine.2d.psdimporter` and `com.xengine.xui`.

## Reproduce from the PSD

```
xengine-cli xui-from-psd --project <root> --doc Assets/Art/login-demo.psd \
  --screen LoginDemo --out UI/logindemo
xengine-cli xui-scaffold --project <root> --doc UI/logindemo/LoginDemo.xui \
  --screen LoginDemo --out UI/logindemo
```

## Layer naming rules

`btn_x=Label` → button (a group's `txt_` child becomes the label instead of baked pixels),
`txt_x=Content` → text (dominant colour, layer-height font size), `input_x=Placeholder` → input
field, `img_x` or unprefixed pixel layer → image, unprefixed group → frame, `#skip` / hidden /
empty layers → reported, never silently dropped.
