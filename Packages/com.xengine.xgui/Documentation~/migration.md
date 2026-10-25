# XGUI Migration Guide

## Scan first

Use `Tools/XGUI/Scan Legacy UI`. The scanner is read-only. It reports:

- package components already on the scene
- Runtime compatibility components that can upgrade
- missing serialized XGUI types

Results keep paths and type names only, never live component references.

## Safe automatic upgrade

`Tools/XGUI/Upgrade Legacy UI in Scene` upgrades mapped components in one Undo step:

- Graphic / Image / Text
- Button, Slider, Scrollbar, ScrollRect, InputField
- RectMask and layout groups

The upgrader copies serialized fields, preserves component identifiers, and redirects references between upgraded components. `CanvasRenderer` may be added as a support component.

## Manual: Dropdown

Do not auto-upgrade `UIDropdown`. A valid package Dropdown needs:

1. a disabled item-template row
2. an independent overlay root parented to the Canvas

Create `GameObject/UI/Dropdown`, then copy options and value by hand.

## After delete / reinstall

Removing the package leaves old scenes loadable as missing types. Reinstalling `com.xengine.xgui` restores the package facades through the type redirects. Package GUIDs on default sprite/font stay stable across reinstalls.
