// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// ArkTS glue between the XEngine ads native bridge (libxengine_ads.so) and the Pangle
// (穿山甲/CSJ) HarmonyOS SDK. Copied into the generated Stage project by the ads package's
// Stage-project contributor, which also wires `setAdsContext(this.context)` into EntryAbility,
// injects `openadsdk.har` into entry/libs + oh-package.json5, and adds module permissions.
//
// PROTOCOL: identical to XengineAdsHuawei.ets (see its header for the JSON shapes).
//
// VENDOR-API STATUS (2026-09): the CSJ HarmonyOS SDK (openadsdk.har) is distributed through
// CSJ's own portal + private ohpm channels behind a media account; the exact ArkTS surface is
// documented only inside their `harmonyadsdk` demo project (csjplatform.com → GroMore Harmony
// 集成文档 → SDK 下载). Until that demo is available to this integration, every vendor call
// below reports a structured `error` event through the bridge — the full path (bridge →
// protocol → provider → events → payload settings) is live and verifiable on device, and
// wiring the vendor calls is a contained edit to the three `handle*` functions marked
// VENDOR-API, following XengineAdsHuawei.ets as the structural reference.

import xengineAds from 'libxengine_ads.so';
import { common } from '@kit.AbilityKit';

const PROTOCOL_VERSION = 1;
const VENDOR_API_MESSAGE = 'pangle vendor API not wired yet; fill the VENDOR-API handlers in ' +
  'Packages/com.xengine.ads/Native/glue/XengineAdsPangle.ets from the CSJ harmonyadsdk demo';

let adsContext: common.UIAbilityContext | null = null;
let sdkInitialized = false;

export function setAdsContext(context: common.UIAbilityContext): void {
  adsContext = context;
}

function emit(json: string): void {
  xengineAds.ads_emit_event(json);
}

function emitResult(id: number, type: string, ok: boolean, error: string, reward: boolean): void {
  let payload: Record<string, Object> = {
    'v': PROTOCOL_VERSION,
    'id': id,
    'type': type,
    'ok': ok
  };
  if (error !== '') {
    payload['error'] = error;
  }
  if (reward) {
    payload['reward'] = true;
  }
  emit(JSON.stringify(payload));
}

// VENDOR-API: initialize the CSJ SDK here once the demo provides the exact call, e.g.
//   import { TTAdSdk } from 'openadsdk';
//   TTAdSdk.init({ context: adsContext, appId: <from request payload>, ... });
// Success must flip `sdkInitialized` so load/show report a precise error instead.
function handleInitialize(request: Record<string, Object>): void {
  sdkInitialized = adsContext !== null;
  emitResult(Number(request['id']), 'init-result', sdkInitialized,
    sdkInitialized ? '' : 'no UIAbility context registered', false);
}

// VENDOR-API: reward-video load via the CSJ HarmonyOS API (demo: RewardVideo sample),
// then emitResult(id, 'load-result', …) from the SDK's load callback.
function handleLoad(request: Record<string, Object>): void {
  if (!sdkInitialized) {
    emitResult(Number(request['id']), 'load-result', false, 'pangle SDK not initialized', false);
    return;
  }
  emitResult(Number(request['id']), 'load-result', false, VENDOR_API_MESSAGE, false);
}

// VENDOR-API: reward-video/interstitial show (demo: corresponding show sample), emit
// 'state' show-start/show-end plus show-result with the reward flag from the SDK callback.
function handleShow(request: Record<string, Object>): void {
  emitResult(Number(request['id']), 'show-result', false, VENDOR_API_MESSAGE, false);
}

function handleRequest(json: string): void {
  let request: Record<string, Object> = JSON.parse(json) as Record<string, Object>;
  const version = Number(request['v']);
  if (version !== PROTOCOL_VERSION) {
    emitResult(Number(request['id']), 'error', false, `protocol version ${version} unsupported`, false);
    return;
  }
  const op = request['op'] as string;
  if (op === 'initialize') {
    handleInitialize(request);
  } else if (op === 'load') {
    handleLoad(request);
  } else if (op === 'show') {
    handleShow(request);
  } else if (op === 'close') {
    emitResult(Number(request['id']), 'close-result', true, '', false);
  } else {
    emitResult(Number(request['id']), 'error', false, 'unknown op ' + op, false);
  }
}

xengineAds.ads_register_bridge(handleRequest);
