import { common } from '@kit.AbilityKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { display, window } from '@kit.ArkUI';
import xengine from 'libxengine_host.so';

const DOMAIN: number = 0x3500;
const TAG: string = 'XEngine.Index';

// XEngine HarmonyOS Stage index page.
//
// Owns the XComponent whose native surface is rendered by libxengine_host.so.
// The native module (xengine_host, built from entry/src/main/cpp/xengine/
// xengine_host_napi.cpp) registers XComponent surface/lifecycle/input callbacks
// and a per-frame OnFrame callback from C++; it also exposes the ArkTS-callable
// exports below. This page:
//   1. Resolves the engine content root (the bundled rawfile xengine assets).
//   2. On aboutToAppear, asks the native module to configure the player. The
//      player attaches its Vulkan surface when the XComponent's
//      OnSurfaceCreated fires (handled in C++).
//   3. Forwards onShow/onHide to setResumed so the engine pauses/resumes its
//      frame thread with the page visibility.
//   4. Bridges screen orientation both ways: engine orientation requests
//      (Screen.Orientation / PlayerSettings defaults) are polled from the
//      native module and applied through window.setPreferredOrientation, and
//      display rotation changes are reported back so the engine resolves the
//      current physical orientation.
//   5. On aboutToDisappear, destroys the player.

// Engine orientation request codes (xengine_player_abi.h).
const ORIENTATION_PORTRAIT: number = 1;
const ORIENTATION_PORTRAIT_UPSIDE_DOWN: number = 2;
const ORIENTATION_LANDSCAPE_LEFT: number = 3;
const ORIENTATION_LANDSCAPE_RIGHT: number = 4;
const ORIENTATION_AUTO_ROTATION: number = 5;

@Entry
@Component
struct Index {
  // The XComponent id MUST match the second argument to
  // OH_NativeXComponent_GetXComponentId expectations; libxengine_host registers
  // its callbacks against the component surfaced through the NAPI export named
  // OH_NATIVE_XCOMPONENT_OBJ, which hvigor wires from libraryname + id.
  private xComponentId: string = 'xengine_surface';
  private contentRoot: string = '';
  private configured: boolean = false;
  private orientationTimer: number = -1;
  private keepScreenOnRequested: boolean = false;
  private keepScreenOnApplied: boolean = false;
  private keepScreenOnPending: boolean = false;
  private pageVisible: boolean = true;
  private orientationBridgeBroken: boolean = false;
  private lastRequestedOrientation: number = -1;
  private lastAllowedMask: number = 0;
  private lastAppliedWindowOrientation: window.Orientation = window.Orientation.UNSPECIFIED;
  private lastReportedRotation: number = -1;
  private orientationForcedFixed: boolean = false;

  aboutToAppear(): void {
    this.contentRoot = this.resolveContentRoot();
    try {
      let ok: boolean = xengine.configurePlayer(this.contentRoot);
      this.configured = ok;
      hilog.info(DOMAIN, TAG, 'configurePlayer(%{public}s) -> %{public}s', this.contentRoot, ok.toString());
    } catch (e) {
      hilog.error(DOMAIN, TAG, 'configurePlayer threw: %{public}s', (e as Error)?.message ?? String(e));
    }
    this.startOrientationBridge();
  }

  aboutToDisappear(): void {
    this.pageVisible = false;
    this.applyKeepScreenOn();
    this.stopOrientationBridge();
    try {
      xengine.destroyPlayer();
      hilog.info(DOMAIN, TAG, '%{public}s', 'destroyPlayer returned');
    } catch (e) {
      hilog.error(DOMAIN, TAG, 'destroyPlayer threw: %{public}s', (e as Error)?.message ?? String(e));
    }
    this.configured = false;
  }

  onPageShow(): void {
    this.pageVisible = true;
    this.applyKeepScreenOn();
    if (!this.configured) return;
    try {
      xengine.setResumed(true);
      hilog.info(DOMAIN, TAG, '%{public}s', 'setResumed(true)');
    } catch (e) {
      hilog.error(DOMAIN, TAG, 'setResumed(true) threw: %{public}s', (e as Error)?.message ?? String(e));
    }
  }

  onPageHide(): void {
    this.pageVisible = false;
    this.applyKeepScreenOn();
    if (!this.configured) return;
    try {
      xengine.setResumed(false);
      hilog.info(DOMAIN, TAG, '%{public}s', 'setResumed(false)');
    } catch (e) {
      hilog.error(DOMAIN, TAG, 'setResumed(false) threw: %{public}s', (e as Error)?.message ?? String(e));
    }
  }

  // The engine reads assets from the HAP's resfile tree. OHOS unpacks the
  // entry/src/main/resources/resfile/ directory into the app sandbox at install
  // time and exposes its absolute on-device path through ctx.resourceDir. Unlike
  // rawfile (which is only reachable via the ResourceManager NDK API), resfile
  // contents are real files on disk that System.IO.File/Directory can read.
  private resolveContentRoot(): string {
    let ctx = getContext(this) as common.UIAbilityContext;
    return ctx.resourceDir;
  }

  // --- Screen orientation bridge ------------------------------------------------------
  //
  // Engine -> OS: the native module stores packed requests; poll and apply them.
  // OS -> engine: display rotation changes map to the engine's physical
  // orientation codes and are pushed through setDisplayOrientation.

  private startOrientationBridge(): void {
    try {
      display.on('change', () => this.applyDisplayOrientation());
      this.applyDisplayOrientation();
    } catch (e) {
      hilog.error(DOMAIN, TAG, 'orientation watcher failed: %{public}s', (e as Error)?.message ?? String(e));
    }
    this.orientationTimer = setInterval(() => this.pollOrientationRequest(), 150);
  }

  private stopOrientationBridge(): void {
    if (this.orientationTimer >= 0) {
      clearInterval(this.orientationTimer);
      this.orientationTimer = -1;
    }
    try {
      display.off('change');
    } catch (e) {
      // The watcher is gone with the window; nothing to detach.
    }
  }

  private pollOrientationRequest(): void {
    if (this.configured) {
      let awake: number = xengine.takeKeepScreenOn();
      if (awake >= 0) this.keepScreenOnRequested = awake !== 0;
      this.applyKeepScreenOn();
    }
    if (this.orientationBridgeBroken || !this.configured) return;
    let packed: number = -1;
    try {
      packed = xengine.takePreferredOrientation();
    } catch (e) {
      // Native host predates the orientation bridge; stop polling for good.
      this.orientationBridgeBroken = true;
      return;
    }
    if (packed < 0) return;
    this.lastRequestedOrientation = packed & 0xFF;
    this.lastAllowedMask = (packed >> 8) & 0xF;
    this.orientationForcedFixed = false;
    hilog.info(
      DOMAIN, TAG,
      'orientation request=%{public}s allowedMask=0x%{public}s',
      this.lastRequestedOrientation.toString(),
      this.lastAllowedMask.toString(16));
    this.applyEngineOrientationRequest();
  }

  private applyKeepScreenOn(): void {
    let desired: boolean = this.pageVisible && this.keepScreenOnRequested;
    if (this.keepScreenOnPending || desired === this.keepScreenOnApplied) return;
    this.keepScreenOnPending = true;
    let ctx = getContext(this) as common.UIAbilityContext;
    window.getLastWindow(ctx).then(w => w.setWindowKeepScreenOn(desired))
      .then(() => {
        this.keepScreenOnApplied = desired;
        this.keepScreenOnPending = false;
        hilog.info(DOMAIN, TAG, 'keepScreenOn=%{public}s', desired.toString());
        this.applyKeepScreenOn();
      })
      .catch((e: Error) => {
        this.keepScreenOnPending = false;
        hilog.error(DOMAIN, TAG, 'keepScreenOn failed: %{public}s', e.message);
      });
  }

  private applyEngineOrientationRequest(): void {
    let orientation = this.resolvePreferredOrientation(this.lastRequestedOrientation, this.lastAllowedMask);
    if (orientation === window.Orientation.UNSPECIFIED) return;
    this.setWindowOrientation(orientation);
  }

  private resolvePreferredOrientation(requested: number, allowedMask: number): window.Orientation {
    switch (requested) {
      case ORIENTATION_PORTRAIT:
        return window.Orientation.PORTRAIT;
      case ORIENTATION_PORTRAIT_UPSIDE_DOWN:
        return window.Orientation.PORTRAIT_INVERTED;
      case ORIENTATION_LANDSCAPE_LEFT:
        return window.Orientation.LANDSCAPE;
      case ORIENTATION_LANDSCAPE_RIGHT:
        return window.Orientation.LANDSCAPE_INVERTED;
      case ORIENTATION_AUTO_ROTATION: {
        // AUTO_ROTATION follows the sensor like Unity's
        // AutoRotation. A single allowed orientation locks directly; restricted
        // sets that keep more than one orientation cannot be expressed as one OS
        // mode (HarmonyOS has no sensor-within-a-subset orientation), so
        // disallowed poses are bounced back in applyDisplayOrientation.
        let portraitBits = (allowedMask & 0x3);
        let landscapeBits = (allowedMask & 0xC);
        let allowedCount = this.bitCount(portraitBits) + this.bitCount(landscapeBits);
        if (allowedCount === 0) {
          return window.Orientation.UNSPECIFIED;
        }
        if (allowedCount === 1) {
          return this.fallbackOrientationForMask(allowedMask);
        }
        return window.Orientation.AUTO_ROTATION;
      }
      default:
        return window.Orientation.UNSPECIFIED;
    }
  }

  private bitCount(value: number): number {
    let count: number = 0;
    if ((value & 0x1) !== 0) count++;
    if ((value & 0x2) !== 0) count++;
    return count;
  }

  private fallbackOrientationForMask(allowedMask: number): window.Orientation {
    if ((allowedMask & 0x1) !== 0) return window.Orientation.PORTRAIT;
    if ((allowedMask & 0x2) !== 0) return window.Orientation.PORTRAIT_INVERTED;
    if ((allowedMask & 0x4) !== 0) return window.Orientation.LANDSCAPE;
    if ((allowedMask & 0x8) !== 0) return window.Orientation.LANDSCAPE_INVERTED;
    return window.Orientation.UNSPECIFIED;
  }

  private setWindowOrientation(orientation: window.Orientation): void {
    if (orientation === this.lastAppliedWindowOrientation) return;
    this.lastAppliedWindowOrientation = orientation;
    let ctx = getContext(this) as common.UIAbilityContext;
    window.getLastWindow(ctx)
      .then(w => w.setPreferredOrientation(orientation))
      .then(() => hilog.info(
        DOMAIN, TAG, 'setPreferredOrientation success=%{public}s', orientation.toString()))
      .catch((e: Error) => {
        this.lastAppliedWindowOrientation = window.Orientation.UNSPECIFIED;
        hilog.error(
          DOMAIN, TAG, 'setPreferredOrientation failed: %{public}s', e?.message ?? String(e));
      });
  }

  private applyDisplayOrientation(): void {
    if (!this.configured) return;
    let physical: number = 0;
    try {
      let screen = display.getDefaultDisplaySync();
      // Display.rotation is a quarter-turn index (0..3), not an angle in degrees.
      let rotation: number = screen.rotation * 90;
      if (rotation === this.lastReportedRotation) return;
      this.lastReportedRotation = rotation;
      let naturalPortrait = rotation === 90 || rotation === 270
        ? screen.width > screen.height
        : screen.height >= screen.width;
      physical = this.physicalOrientation(naturalPortrait, rotation);
      if (physical < ORIENTATION_PORTRAIT || physical > ORIENTATION_LANDSCAPE_RIGHT) return;
      hilog.info(
        DOMAIN, TAG,
        'display rotation=%{public}s size=%{public}sx%{public}s naturalPortrait=%{public}s physical=%{public}s',
        rotation.toString(),
        screen.width.toString(),
        screen.height.toString(),
        naturalPortrait.toString(),
        physical.toString());
      xengine.setDisplayOrientation(physical);
    } catch (e) {
      hilog.error(DOMAIN, TAG, 'display orientation report failed: %{public}s', (e as Error)?.message ?? String(e));
      return;
    }
    this.enforceAutoRotationMask(physical);
  }

  // While auto-rotating with a restricted set, bounce the window back when the
  // device settles in a disallowed pose, and re-arm rotation once it is allowed.
  private enforceAutoRotationMask(physical: number): void {
    if (this.lastRequestedOrientation !== ORIENTATION_AUTO_ROTATION) return;
    if (this.lastAllowedMask === 0xF || this.lastAllowedMask === 0) return;
    let allowed = (this.lastAllowedMask & (1 << (physical - 1))) !== 0;
    if (allowed) {
      if (this.orientationForcedFixed) {
        this.orientationForcedFixed = false;
        this.applyEngineOrientationRequest();
      }
      return;
    }
    let fallback = this.fallbackOrientationForMask(this.lastAllowedMask);
    if (fallback === window.Orientation.UNSPECIFIED) return;
    this.orientationForcedFixed = true;
    this.setWindowOrientation(fallback);
  }

  private physicalOrientation(naturalPortrait: boolean, rotation: number): number {
    if (naturalPortrait) {
      if (rotation === 90) return ORIENTATION_LANDSCAPE_LEFT;
      if (rotation === 180) return ORIENTATION_PORTRAIT_UPSIDE_DOWN;
      if (rotation === 270) return ORIENTATION_LANDSCAPE_RIGHT;
      return ORIENTATION_PORTRAIT;
    }
    if (rotation === 90) return ORIENTATION_PORTRAIT_UPSIDE_DOWN;
    if (rotation === 180) return ORIENTATION_LANDSCAPE_RIGHT;
    if (rotation === 270) return ORIENTATION_PORTRAIT;
    return ORIENTATION_LANDSCAPE_LEFT;
  }

  build() {
    Column() {
      XComponent({
        id: this.xComponentId,
        type: XComponentType.SURFACE,
        libraryname: 'xengine_host'
      })
        .onLoad(() => {
          hilog.info(DOMAIN, TAG, '%{public}s', 'XComponent onLoad (native surface ready)');
        })
        .width('100%')
        .height('100%')
    }
    .width('100%')
    .height('100%')
  }
}
