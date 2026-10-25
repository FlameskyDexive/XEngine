# Login Screen

One screen showing the pieces you will use on almost every real one: theme tokens, a stretched
backdrop, a centred `VStack` that grows to its content, a template used twice with parameterised ids,
a horizontal action row, and a status line the controller writes to.

Copy `Login.xui` anywhere under `Assets/` and validate it:

```bash
xengine-cli xui-validate --project <root> --doc Assets/UI/login/Login.xui
```

Note `size="480x_"` on the panel: width is pinned, height is left to the stack. And note that
`XInputAccount` / `XInputPassword` are named at the usage site rather than inside the template — a
template that hard-coded its inner ids could only ever be used once per screen.
