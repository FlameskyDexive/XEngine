# com.xengine.ads — Stage contribution evidence (2026-09-17)

Template source: `D:\Engine\XEngine\XtsHotUpdateDemo\ThirdParty\OpenHarmonySdk\ohos-local-20260911-awake4\templates\stage-player` (real SDK Bundle stage-player templates).

## Huawei Ads Kit (huawei-adskit)

Contribution errors: none

### Files added/changed by the contributor

```
[x] entry/src/main/ets/xengineads/XengineAdsHuawei.ets
[ ] entry/src/main/ets/xengineads/XengineAdsPangle.ets
[x] entry/src/main/ets/entryability/EntryAbility.ets
[x] entry/src/main/module.json5
[x] entry/oh-package.json5
[ ] entry/libs/openadsdk.har
[x] entry/src/main/resources/resfile/Settings/Ads.json
```

### EntryAbility.ets (contributed)

```ts
import { AbilityConstant, ConfigurationConstant, UIAbility, Want } from '@kit.AbilityKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { window } from '@kit.ArkUI';
import { setAdsContext } from '../xengineads/XengineAdsHuawei';

const DOMAIN: number = 0xD003500;
const TAG: string = 'XEngine.EntryAbility';

// XEngine HarmonyOS entry ability.
//
// The Stage entry ability loads the single XComponent page (pages/Index), which
// owns the libxengine_host native surface. Engine lifecycle (configure/resume/
// destroy) is driven from Index.ets; this ability only forwards window-stage
// creation and keeps the color mode unset so the engine controls presentation.
export default class EntryAbility extends UIAbility {
  onCreate(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    setAdsContext(this.context);
    this.context.getApplicationContext().setColorMode(ConfigurationConstant.ColorMode.COLOR_MODE_NOT_SET);
    hilog.info(DOMAIN, TAG, '%{public}s', 'EntryAbility onCreate');
  }

  onDestroy(): void {
    hilog.info(DOMAIN, TAG, '%{public}s', 'EntryAbility onDestroy');
  }

  onWindowStageCreate(windowStage: window.WindowStage): void {
    hilog.info(DOMAIN, TAG, '%{public}s', 'EntryAbility onWindowStageCreate');
    windowStage.loadContent('pages/Index', (err) => {
      if (err.code) {
        hilog.error(DOMAIN, TAG, 'Failed to load pages/Index: %{public}s', JSON.stringify(err) ?? '');
        return;
      }
      windowStage.getMainWindow().then(w => {
        w.setWindowLayoutFullScreen(true);
      });
      hilog.info(DOMAIN, TAG, '%{public}s', 'Loaded pages/Index');
    });
  }

  onWindowStageDestroy(): void {
    hilog.info(DOMAIN, TAG, '%{public}s', 'EntryAbility onWindowStageDestroy');
  }

  onForeground(): void {
    hilog.info(DOMAIN, TAG, '%{public}s', 'EntryAbility onForeground');
  }

  onBackground(): void {
    hilog.info(DOMAIN, TAG, '%{public}s', 'EntryAbility onBackground');
  }
}
```

### module.json5 (permissions injected)

```json5
{
  "module": {
    "name": "entry",
    "type": "entry",
    "description": "$string:module_desc",
    "mainElement": "EntryAbility",
    "deviceTypes": [
      "phone",
      "tablet",
      "2in1",
      "car"
    ],
    "deliveryWithInstall": true,
    "installationFree": false,
    "pages": "$profile:main_pages",
        "requestPermissions": [ { "name": "ohos.permission.INTERNET" } ],
"abilities": [
      {
        "name": "EntryAbility",
        "srcEntry": "./ets/entryability/EntryAbility.ets",
        "description": "$string:EntryAbility_desc",
        "icon": "$media:layered_image",
        "label": "$string:EntryAbility_label",
        "startWindowIcon": "$media:startIcon",
        "startWindowBackground": "$color:start_window_background",
        "exported": true,
        "orientation": "landscape",
        "skills": [
          {
            "entities": [
              "entity.system.home"
            ],
            "actions": [
              "action.system.home"
            ]
          }
        ]
      }
    ]
  }
}
```

### Settings/Ads.json (runtime payload)

```json
{
  "schema": "xengine.ads.settings.v1",
  "providerId": "huawei-adskit",
  "useTestAds": true,
  "appId": "110xxxxx",
  "rewardVideoSlotId": "testb4zuon2bty",
  "interstitialSlotId": "testb4zcw8ocpa"
}
```

## Pangle (pangle)

Contribution errors: none

### Files added/changed by the contributor

```
[ ] entry/src/main/ets/xengineads/XengineAdsHuawei.ets
[x] entry/src/main/ets/xengineads/XengineAdsPangle.ets
[x] entry/src/main/ets/entryability/EntryAbility.ets
[x] entry/src/main/module.json5
[x] entry/oh-package.json5
[x] entry/libs/openadsdk.har
[x] entry/src/main/resources/resfile/Settings/Ads.json
```

### EntryAbility.ets (contributed)

```ts
import { AbilityConstant, ConfigurationConstant, UIAbility, Want } from '@kit.AbilityKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { window } from '@kit.ArkUI';
import { setAdsContext } from '../xengineads/XengineAdsPangle';

const DOMAIN: number = 0xD003500;
const TAG: string = 'XEngine.EntryAbility';

// XEngine HarmonyOS entry ability.
//
// The Stage entry ability loads the single XComponent page (pages/Index), which
// owns the libxengine_host native surface. Engine lifecycle (configure/resume/
// destroy) is driven from Index.ets; this ability only forwards window-stage
// creation and keeps the color mode unset so the engine controls presentation.
export default class EntryAbility extends UIAbility {
  onCreate(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    setAdsContext(this.context);
    this.context.getApplicationContext().setColorMode(ConfigurationConstant.ColorMode.COLOR_MODE_NOT_SET);
    hilog.info(DOMAIN, TAG, '%{public}s', 'EntryAbility onCreate');
  }

  onDestroy(): void {
    hilog.info(DOMAIN, TAG, '%{public}s', 'EntryAbility onDestroy');
  }

  onWindowStageCreate(windowStage: window.WindowStage): void {
    hilog.info(DOMAIN, TAG, '%{public}s', 'EntryAbility onWindowStageCreate');
    windowStage.loadContent('pages/Index', (err) => {
      if (err.code) {
        hilog.error(DOMAIN, TAG, 'Failed to load pages/Index: %{public}s', JSON.stringify(err) ?? '');
        return;
      }
      windowStage.getMainWindow().then(w => {
        w.setWindowLayoutFullScreen(true);
      });
      hilog.info(DOMAIN, TAG, '%{public}s', 'Loaded pages/Index');
    });
  }

  onWindowStageDestroy(): void {
    hilog.info(DOMAIN, TAG, '%{public}s', 'EntryAbility onWindowStageDestroy');
  }

  onForeground(): void {
    hilog.info(DOMAIN, TAG, '%{public}s', 'EntryAbility onForeground');
  }

  onBackground(): void {
    hilog.info(DOMAIN, TAG, '%{public}s', 'EntryAbility onBackground');
  }
}
```

### module.json5 (permissions injected)

```json5
{
  "module": {
    "name": "entry",
    "type": "entry",
    "description": "$string:module_desc",
    "mainElement": "EntryAbility",
    "deviceTypes": [
      "phone",
      "tablet",
      "2in1",
      "car"
    ],
    "deliveryWithInstall": true,
    "installationFree": false,
    "pages": "$profile:main_pages",
        "requestPermissions": [ { "name": "ohos.permission.INTERNET" } ],
"abilities": [
      {
        "name": "EntryAbility",
        "srcEntry": "./ets/entryability/EntryAbility.ets",
        "description": "$string:EntryAbility_desc",
        "icon": "$media:layered_image",
        "label": "$string:EntryAbility_label",
        "startWindowIcon": "$media:startIcon",
        "startWindowBackground": "$color:start_window_background",
        "exported": true,
        "orientation": "landscape",
        "skills": [
          {
            "entities": [
              "entity.system.home"
            ],
            "actions": [
              "action.system.home"
            ]
          }
        ]
      }
    ]
  }
}
```

### Settings/Ads.json (runtime payload)

```json
{
  "schema": "xengine.ads.settings.v1",
  "providerId": "pangle",
  "useTestAds": true,
  "appId": "pg-app",
  "rewardVideoSlotId": "pg-reward",
  "interstitialSlotId": "pg-inter"
}
```

### entry/oh-package.json5 (har dependency injected)

```json5
{
  "name": "entry",
  "version": "1.0.0",
  "description": "XEngine HarmonyOS entry module. Native host (libxengine_host.so) is built from entry/src/main/cpp/xengine/ and exports the xengine_host NAPI module.",
  "main": "",
  "author": "",
  "license": "Apache-2.0",
  "dependencies": {
        "openadsdk": "file:./libs/openadsdk.har",
    // ArkTS imports the xengine_host NAPI module via 'libxengine_host.so'; the
    // type declarations live under src/main/cpp/types/libxengine_host.
    "libxengine_host.so": "file:./src/main/cpp/types/libxengine_host"
  }
}
```

