# Pixel Button

A two-state 9-slice button skin authored entirely as text. `[normal]` has its highlight on the top and
left; `[pressed]` swaps it to the bottom and right, which is what makes the button read as pushed in.

Look at it before you edit it:

```bash
xengine-cli pxl-render --project <root> --doc Assets/UI/art/button.pxl --scale 12
```

Then read the PNG back. Checking the characters proves the file parses; only the render tells you the
bevel points the right way.

Both sections share one texture and one palette, so the states stay together through import and
atlasing. Reference the result from a `.xui`:

```xml
<Btn id="XBtnOk" sprite="UI/art/button.pxl" type="sliced">OK</Btn>
```
