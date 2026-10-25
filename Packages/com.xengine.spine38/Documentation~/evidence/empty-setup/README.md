# 0.1.3 empty setup attachment acceptance

The prior renderer discarded an atlas-backed skeleton whenever all setup slots were empty, and cleared its imported data cache. Valid effect animations could therefore never begin. The same check also ran before SkeletonAnimation selected its initial skin.

The renderer now retains the successfully parsed skeleton and permits zero draw batches until animation or skin selection reveals attachments. Parsing errors still propagate. The test constructs an atlas-backed imported skeleton, reveals a region at 0.25 seconds, hides it at 0.75 seconds and checks that skeleton and cached data identity remain stable. The old code reproduces a failure after clearing the cache (baseline.log); all 14 package tests pass after the fix (tests.log).

Live acceptance in the Icey gallery, Windows editor: the real source-converted NewPlayerTestEffect prefab initializes successfully. RollGround at zero has no visible effect (empty.png), while TrackTime=0.8653439 displays its region attachments (revealed.png). The camera is at (0,100,-10), orthographic size 12; component TimeScale=0 freezes the explicit sample. This accepts initialization and reveal behavior, not original Unity material/visual fidelity. Color parity remains an Icey migration gate.

Validation used a local package pin before npm publication; registry install is verified separately after publishing. No engine-core source changes belong to this package fix.
