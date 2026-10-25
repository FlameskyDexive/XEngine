// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// ArkTS glue between the XEngine ads native bridge (libxengine_ads.so) and the HarmonyOS
// system Ads Kit (@kit.AdsKit). Copied into the generated Stage project by the ads package's
// Stage-project contributor, which also wires `setAdsContext(this.context)` into EntryAbility
// and adds ohos.permission.INTERNET to module.json5.
//
// Protocol (versioned JSON strings, mirrored by HarmonyOSAdsProtocol in C#):
//   request  { "v":1, "id":N, "op":"initialize"|"load"|"show"|"close",
//              "adType":"reward"|"interstitial", "slotId":"…", "test":bool }
//   event    { "v":1, "id":N, "type":"init-result"|"load-result"|"show-result", "ok":bool,
//              "error":"…", "reward":bool }
//   event    { "v":1, "type":"state", "state":"show-start"|"show-end", "adType":"…",
//              "reward":bool }

import xengineAds from 'libxengine_ads.so';
import { advertising } from '@kit.AdsKit';
import { common } from '@kit.AbilityKit';

const PROTOCOL_VERSION = 1;

let adsContext: common.UIAbilityContext | null = null;
let adLoader: advertising.AdLoader | null = null;
// One cached ad per format; a fresh load replaces the cached one (vendor validity: Huawei
// requires show within 1 h of load — the C# provider enforces reloads).
let cachedRewardAd: advertising.Advertisement | null = null;
let cachedInterstitialAd: advertising.Advertisement | null = null;

export function setAdsContext(context: common.UIAbilityContext): void {
  adsContext = context;
}

function emit(json: string): void {
  xengineAds.ads_emit_event(json);
}

function adTypeToken(adType: string): advertising.AdType {
  return adType === 'interstitial' ? advertising.AdType.INTERSTITIAL_AD : advertising.AdType.REWARDED_AD;
}

function cachedAd(adType: string): advertising.Advertisement | null {
  return adType === 'interstitial' ? cachedInterstitialAd : cachedRewardAd;
}

function setCachedAd(adType: string, value: advertising.Advertisement | null): void {
  if (adType === 'interstitial') {
    cachedInterstitialAd = value;
  } else {
    cachedRewardAd = value;
  }
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

function emitState(state: string, adType: string, reward: boolean): void {
  let payload: Record<string, Object> = {
    'v': PROTOCOL_VERSION,
    'type': 'state',
    'state': state,
    'adType': adType
  };
  if (reward) {
    payload['reward'] = true;
  }
  emit(JSON.stringify(payload));
}

function handleLoad(request: Record<string, Object>): void {
  if (adsContext === null) {
    emitResult(Number(request['id']), 'load-result', false, 'no UIAbility context registered', false);
    return;
  }
  const adType = request['adType'] as string;
  const requestParams: advertising.AdRequestParams = {
    adId: request['slotId'] as string,
    adType: adTypeToken(adType)
  };
  const adOptions: advertising.AdOptions = {
    adCount: 1
  };
  if (adLoader === null) {
    adLoader = advertising.createAdLoader({ context: adsContext });
  }
  adLoader.loadAd(requestParams, adOptions, {
    onLoad: (ads: advertising.Advertisement[]) => {
      if (ads === null || ads.length === 0) {
        emitResult(Number(request['id']), 'load-result', false, 'no fill', false);
        return;
      }
      setCachedAd(adType, ads[0]);
      emitResult(Number(request['id']), 'load-result', true, '', false);
    },
    onError: (errorCode: number, errorMsg: string) => {
      emitResult(Number(request['id']), 'load-result', false, `errorCode=${errorCode} ${errorMsg}`, false);
    }
  } as advertising.AdLoadListener);
}

function handleShow(request: Record<string, Object>): void {
  const adType = request['adType'] as string;
  const ad = cachedAd(adType);
  if (ad === null) {
    emitResult(Number(request['id']), 'show-result', false, 'nothing loaded for ' + adType, false);
    return;
  }
  setCachedAd(adType, null);
  emitState('show-start', adType, false);
  ad.show();
  // v1: a completed rewarded presentation counts the reward. Server-side rewardVerify
  // wiring is validated during device acceptance (see Documentation~/index.md).
  const reward = adType === 'reward';
  emitState('show-end', adType, reward);
  emitResult(Number(request['id']), 'show-result', true, '', reward);
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
    // The system Ads Kit needs no explicit SDK init; readiness = a context is registered.
    emitResult(Number(request['id']), 'init-result', adsContext !== null,
      adsContext === null ? 'no UIAbility context registered' : '', false);
  } else if (op === 'load') {
    handleLoad(request);
  } else if (op === 'show') {
    handleShow(request);
  } else if (op === 'close') {
    setCachedAd('reward', null);
    setCachedAd('interstitial', null);
    emitResult(Number(request['id']), 'close-result', true, '', false);
  } else {
    emitResult(Number(request['id']), 'error', false, 'unknown op ' + op, false);
  }
}

xengineAds.ads_register_bridge(handleRequest);
