# XGUI Samples

Hidden Unity-style sample folder. PackageManager, asmdef discovery, and ScriptCompiler skip any directory whose name ends with `~`, so these files never compile into a project.

## Controls Gallery

`ControlsGallery/XGuiControlsGallery.xgui-template.json` is a package template that builds one Overlay Canvas with Image, Text, Button, InputField, and a horizontal layout row.

Open a project with `com.xengine.xgui` resolved, then instantiate the template. The sample is authoring data only and does not add a runtime asmdef.
