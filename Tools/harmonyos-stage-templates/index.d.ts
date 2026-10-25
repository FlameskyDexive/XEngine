// Type declarations for the xengine_host NAPI module. The native source lives in
// entry/src/main/cpp/xengine/xengine_host_napi.cpp and registers these exports:
//   configurePlayer(contentRoot: string): boolean
//   setResumed(resumed: boolean): boolean
//   destroyPlayer(): boolean
//   textInputActive(): boolean
//   textInputType(): number
//   takePreferredOrientation(): number
//   setDisplayOrientation(orientation: number): boolean
// The XComponent surface/lifecycle/input callbacks are registered from C++ via
// OH_NativeXComponent_RegisterCallback and are NOT exposed to ArkTS directly;
// ArkTS drives engine start/resume/destroy, owns the XComponent node, and bridges
// screen orientation: it polls engine orientation requests (takePreferredOrientation,
// packed per xengine_player_abi.h — low byte request code, bits 8..11 allowed mask)
// and applies them through window.setPreferredOrientation, and reports the physical
// orientation (1=portrait, 2=portrait upside-down, 3=landscape left, 4=landscape
// right) back via setDisplayOrientation whenever the display rotation changes.
export const configurePlayer: (contentRoot: string) => boolean;
export const setResumed: (resumed: boolean) => boolean;
export const destroyPlayer: () => boolean;
export const textInputActive: () => boolean;
export const textInputType: () => number;
export const takePreferredOrientation: () => number;
export const takeKeepScreenOn: () => number;
export const setDisplayOrientation: (orientation: number) => boolean;
