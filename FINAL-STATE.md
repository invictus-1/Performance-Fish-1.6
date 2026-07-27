# Performance Fish + Fishery on RimWorld 1.6 — final state

Both mods build and run against RimWorld 1.6.4871. Upstream has not released 1.6 versions;
this is a local port.

## Required setup

Load order: Harmony → Prepatcher → Fishery → Core (+ DLCs) → other mods → Performance Fish.

**Nothing is disabled.** All patches active.

Startup line confirming it loaded:

```
[Performance Fish] 1.6 build - no skip file (nothing skipped)
```

## ListerBuildings — fixed

`AddPatch` and `RemovePatch` originally crashed to desktop, taking the whole
`Listers.Buildings` class with them (the other eight patches read from a cache only
`AddPatch` fills, so disabling just the writers gives empty building lists — no crash,
silently wrong).

**Cause, confirmed by disassembling the real `Assembly-CSharp.dll`:** 1.6 rewrote
`ListerBuildings.Add` to have **two `ret` instructions and one exception-handling clause**;
`Remove` gained one clause too. These are the new scope-tracker members (`scopes`,
`StartTracker`, `ReleaseTracker`, `Dispose`).

Performance Fish's own prepatch injector (`FishPrepatch.ApplyPostfix`) inserts the postfix
before the last `ret`, rewrites every earlier `ret` into an unconditional `br`, and retargets
any branch pointing at a `ret` — including the `leave` that terminates a protected region. It
has no concept of exception handling clauses. That produced unverifiable IL which JIT'd to
garbage, so `Building.SpawnSetup` jumped to an unresolvable address and the process died with
no managed exception to catch.

**Fix:** both patches converted from `FishPrepatch` (Cecil) to `FishPatch` (Harmony), which
has handled multiple returns and protected regions correctly for years. They also moved into
a new `BuildingsHarmonyPatches : ClassWithFishPatches` host — `ClassWithFishPrepatches` does
not implement `IHasFishPatch`, so nested Harmony patches there are never collected and would
have silently done nothing. Costs slight per-call overhead versus a prepatch.

## Fixes made

**Build configuration** (in `1.6.csproj` files; `Directory.Build.Props` untouched so the
1.2–1.5 projects still build):

- `PublicizerRuntimeStrategies` → `Unsafe`. The default also emits `IgnoresAccessChecksTo`,
  which makes Roslyn ignore accessibility for all of 0Harmony — dragging Harmony's internal
  `Span<T>` into scope against 1.6's new `mscorlib` `Span<T>`. 184 errors from one setting.
- Publicize narrowed to `^(Mono\.|MonoMod\.|HarmonyLib\.)`.
- Corrected modern-BCL names: `_buckets`, `_entries`, `_firstChar`, `RuntimeFieldInfo`.
- `System.Runtime.CompilerServices.Unsafe` range matched to Fishery's `6.*`. Mismatched
  strong-named versions would have failed at load despite compiling fine.

**Fishery:**

- `DangerousGetPinnableReference()` → `GetPinnableReference()`, wrapped in
  `Unsafe.AsRef(in …)` where a writable ref was needed (`ReadOnlySpan` returns `ref readonly`).
- **Dictionary bucket layout.** 1.6 uses the .NET Core 2.1+ layout where `_buckets` stores
  `index + 1` and `0` means empty; 1.5 stored the raw index with `-1`. Bradson's hand-rolled
  lookup used `while (bucket >= 0)`, so an empty bucket read as entry 0 and the chain walked
  one entry off — which could loop forever. All 11 sites now go through one version-guarded
  helper with `(uint)bucket < (uint)_entries.Length` as the bound.
- **`EquatableReferenceType<T>` never selected on 1.6.** Its body compiles to a
  `constrained. callvirt IEquatable<T>.Equals(T)`; reference types share one code body
  (`__Canon`), and resolving that call needs a hidden generic-context argument a raw
  `delegate*` never passes. Reference types now use `Object<T>` (plain `callvirt`), which is
  the same answer for any sane `IEquatable<T>`.

**Performance Fish:**

- `CellIndices` became a struct with `sizeX`/`sizeZ` (was a class with `mapSizeX`/`mapSizeZ`).
- `System.HashCode` is new in 1.6 and collided with `FisheryLib.HashCode` — qualified.
- `ThingDef.hideAtSnowDepth` → `hideAtSnowOrSandDepth`.
- `Verse.MapEvents` is new in 1.6 and collided with `PerformanceFish.Events.MapEvents`.
- `using LudeonTK` was gated `#if V1_5` → `#if !V1_4`.
- Fog grid indexing → `Unity.Collections.NativeBitArray.IsSet()`.
- `ListerHaulables.haulables` is a `HashSet<Thing>` now; cache retyped behind `#if V1_6`.
- `GasGrid.SetDirect` has four overloads in 1.6 — target resolved by explicit parameter types
  (name-only lookup threw `AmbiguousMatchException` at startup).
- `ModsConfig.AreAllActive` gained a second overload — same fix.
- `CompRottable.CompTick` → `CompTickInterval(int)`. 1.6 moved comps to interval ticking;
  `AccessTools.Method` had silently bound to the abstract `ThingComp.CompTick`.
- `BuildableDef.GetHashCode` no longer exists in 1.6 (only on `Def`). The prepatch now skips
  cleanly instead of null-crashing. **Lost optimization** — recoverable by retargeting to
  `Def`, untested.
- `RegionGrid.regionGrid` is lazily allocated in 1.6 → use `DirectGrid`. This was throwing an
  NRE every frame the temperature readout was visible.
- `#if !V1_5` → `#if V1_4` in `AllBuildingsColonistOfDef_Patch`. Under `V1_6` the old
  condition was true, so it compiled the 1.4 branch returning `IEnumerable<Building>` from a
  method whose signature is `List<Building>` — invalid IL. Only `V1_5` conditional in either
  mod.
- Removed three `AsParallel()` calls in `GenTypesPatches`. Besides the deadlock risk,
  `AllSubclasses` shares a mutable `BaseType` field across worker threads — overlapping calls
  would silently return wrong subclass lists.

## Known remaining issues

- `BuildableDef.GetHashCode` caching lost.
- `GenTypesPatches.AllTypes` uses `Monitor.Enter` in a Harmony prefix with the matching
  `Monitor.Exit` in a postfix — bradson's own comment reads `// TODO: replace with finalizer`.
  If the patched method ever throws, that lock is never released and every later access to
  `GenTypes.AllTypes` hangs with idle CPU. Untouched, pre-existing.
- `ModsConfigPatches` fixes a MayRequire steam-suffix bug that **1.6 fixed in vanilla** — the
  build warns `ModsConfig.IsAnyActiveOrEmpty is obsolete, use ModLister.AnyModActiveNoSuffix`.
  Possibly redundant now, possibly double-trimming. Worth revisiting if mod dependency
  detection misbehaves.

## Rebuilding

```powershell
dotnet build "...\Fishery-main\Source\FisheryLib\1.6.csproj" -c Release
dotnet build "...\Performance-Fish-main\Source\PerformanceFish\1.6.csproj" -c Release
```

Fishery first — Performance Fish links against its output. Copy both `1.6\Assemblies\`
folders into the installed mod folders.

## Diagnosing future problems

Create `pf-skip.txt` in RimWorld's Config folder:

```
%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\pf-skip.txt
```

One substring per line; `#` comments a line out. Matches any patch whose full type name
contains the text — a whole class (`Listers.Buildings`) or a single patch
(`Buildings+AddPatch`). Edit, save, relaunch — no rebuild. A line of just `PerformanceFish.`
disables everything, which is the fastest way to answer "is this Performance Fish or another
mod?" The file is optional; absent means nothing is skipped.

Crash to desktop with no managed exception means invalid IL in a patched method — look for
the `<unknown>` frame in the native stack and note what called it. A managed exception with
`TRANSPILER bs.performance` in the trace means a patch applied but is misbehaving.
