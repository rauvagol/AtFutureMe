# Clock face & planetary gear drive

Living reference for AtFutureMe’s clock overlay and concentric gear train.
Read this before changing geometry, paint order, settings, or carrier math — many
constraints are non-obvious and easy to rediscover the hard way.

**Last updated:** 2026-09-20 (docs authored from current code).

---

## 1. Goals of the system

The clock can optionally draw a **concentric multi-stage planetary gear train**
under the face. Design intent:

1. **Kinematically honest** — tooth counts and Willis equations drive rotation;
   animation is not a free-spinning decoration that ignores mesh.
2. **Locked ratio to the second hand** — outer face ring completes one revolution
   over the clock horizon; the center sun tracks **1 rpm** relative to that
   second-hand angle (before `ClockGearAnimSpeed`).
3. **Assemblable** — catalogs and sliders only offer trains / planet counts that
   tip/valley mesh and clear each other at the chosen rim.
4. **Looks-first controls** — planet count, idler offset, compound scale, and
   carrier rates are aesthetics *within* those assembly constraints.

Hands and wedges stay independent of gear anim speed; gears share the second
hand’s unwrapped angle so mesh phase stays locked to wall time.

---

## 2. File map

| Path | Role |
|------|------|
| `Services/ClockGearGeometry.cs` | Catalog, stage solve, seating, Willis, radii, `Build*` wrappers |
| `Services/GearCutter.cs` | ISO involute tooth outlines (external / internal / hollow) |
| `Windows/ClockOverlay.xaml` | Layer stack (`FaceFill` → `Gears` → `Cues` → `OuterDrive` → `Face` → `HourAccents` → `Hands` → `Marks`) |
| `Windows/ClockOverlay.xaml.cs` | Face paint, `AddConcentricPlanetary`, per-frame spin |
| `Models/Models.cs` | `AppSettings` clock/gear fields, carriage enums |
| `AppHost.cs` | `SyncClockOverlay`, gear normalize on settings load |
| `Windows/ConfigWindow.xaml(.cs)` | Aesthetics UI: train dropdown, colors, fiddle sliders |
| `_probe/Program.cs` | Offline probe for compound mul / assemble experiments |

Related but not gear math: horizon / category wedges, lock chrome, attention
tint — still painted in `ClockOverlay.RefreshCore`.

---

## 3. Glossary

| Term | Meaning here |
|------|----------------|
| **Face / outer ring** | Fixed **144T internal** annulus at the clock rim (stage 0 host). |
| **Planet** | Small **6T external** pinion meshing that host internally. Coaxial with the compound. Color: `ClockGearPlanetColor`. |
| **Compound** | Larger external gear on the same center (`Nc`, labeled `6+Nc`). Drives the idler. Color: `ClockGearCompoundColor`. |
| **Compound small / large** | Visual: large = compound body; small = planet pinion on top. |
| **Idler** | External pinion between compound and sun (`Ni = 6` stock). **One per planet** (pairs not assemblable). Color: `ClockGearIdlerColor`. |
| **Sun / inner ring** | Center of a stage. Drawn as a **hollow ring**: exterior teeth mesh idlers; interior hosts the next stage. Last sun is solid / sun-colored and locks second-hand rate. |
| **Host** | Internal tooth set a stage meshes against (face, or previous hollow interior). |
| **Stage / annulus** | One planetary layer: host → planets+compounds → idlers → sun. |
| **Train / progression** | Ordered compound sizes `[Nc…]` that admit an **exact** last sun for ×1440. |
| **Carrier / carousel** | Rotating frame carrying planets+idlers (`ω_c`). |
| **Carriage** | Visual scaffold only (rails, pin-links, cradles…) — not kinematic. |
| **Mesh\*Teeth / Draw\*Teeth** | Tooth counts after pitch caps or ×k scale (drawing / mesh phase). |
| **Kinematic Nc** | Stock compound teeth used for last-stage sun RPM lock. |
| **secScale** | Revolutions per second-hand revolution (≈ RPM if second hand is 1 rpm). |
| **Horizon** | Hours shown on the face; sets outer ring period via `FaceToSecondRatio`. UI offers only divisors of 144 (`HorizonHourOptions`) so colored teeth stay 1:1 with hours. |

---

## 4. Architecture overview

```
Clock horizon (hours)
        │
        ▼
outer ring rate = −1 / (hours × 60)    // FaceRingSecScale (CCW)
        │
        ▼
stages 0…n-1  (compounds Nc_i, planets 6T, idlers 6T)
        │
        ▼
last sun Ns   chosen so overall ratio outer→sun = 1440
        │
        ▼
Willis carrier solve (auto equalize or manual °/min)
        │
        ▼
ClockOverlay paints geometries + RotateTransforms
        │
        ▼
each frame: Angle = base + SecondHandTurn(now) × secScale × AnimSpeed
```

**Radial nest (outer → center):**

```
Face internal ring (144T)
  └─ stage 0: planets + compounds on carrier → idlers → hollow ring 0
       └─ stage 1: … → hollow ring 1
            └─ … → last sun (ExactLastSunTeeth), sun color, lockSunRpm
```

Minimum live train is **2 stages** (`ToothProgression.StageCount`). Overlay always
draws at least stage 0 and 1 when drive is on.

---

## 5. Core ratio math

### 5.1 Exact last sun

`ClockGearGeometry.ExactLastSunTeeth`:

\[
\frac{\omega_{\text{sun}}}{\omega_{\text{outer}}}
  = \frac{144 \cdot \prod N_c}{6^{n} \cdot N_{s,\text{last}}}
  = 1440
\quad\Rightarrow\quad
N_{s,\text{last}} = \frac{\prod N_c}{10 \cdot 6^{n}}
\]

- Intermediate suns **cancel** in the product (each stage’s sun becomes the next
  host). Do **not** lock early ring tooth counts for the overall ratio.
- Product must divide exactly; result must lie in `[MinSunTeeth, MaxSunTeeth]`
  = `[8, 256]` or the train is rejected (`0`).
- Random Nc tuples silently fail — catalogs only keep exact trains.

### 5.2 Outer ring vs horizon

```csharp
FaceToSecondRatio(horizonHours) => max(1, hours) * 60.0;
FaceRingSecScale(horizonHours)  => -1 / ratio
// One face rev over the displayed horizon, counter-clockwise.
// Future marks sit clockwise from Now; fixed hours (and colored teeth) approach
// the top CCW so that at each wall-clock :00 an hour accent is exactly at top.
// 24h → −1/1440; 6h → −1/360.
```

### 5.3 Willis (per stage)

With host teeth `Nh`, planet `Np`, compound `Nc`, sun `Ns`:

\[
R = \frac{N_h}{N_p}\cdot\frac{N_c}{N_s},\quad
\omega_s = R\cdot\omega_h + (1-R)\cdot\omega_c
\]

Overlay relative spins (`CarrierFrameSpins`):

- `planetRel = (Nh/Np) * (hostInertial - carrierSec)`
- **Last stage (`lockSunRpm`):** sun inertial from **kinematic** Nc so center
  tracks 1 rpm; idler locked to that sun (avoids chewing when visual Nc ≠ kinematic).
- **Earlier stages:** idler/sun use **mesh/visual** Nc so a grown compound still
  drives the hollow ring.

### 5.4 Time base

```csharp
SecondHandTurn(now) => now.TimeOfDay.TotalMinutes * (2π);
// Apply: Angle° = baseDeg + SecondHandTurn × secScale × AnimSpeed × (180/π)
```

`ClockGearAnimSpeed` (1–1440) multiplies **gears only**. Hands ignore it.

---

## 6. Geometry pipeline (`ClockGearGeometry` + `GearCutter`)

### 6.1 Constants (do not casually change)

| Constant | Value | Notes |
|----------|-------|-------|
| `FaceRingTeeth` | 144 | Outer host |
| `FacePlanetTeeth` / idler | 6 | Pinions |
| `FaceHourToothStride` | 12 | **Legacy** fixed stride (12 marks). Prefer `HourAccentStride`. |
| `HourAccentStride(teeth, hours)` | computed | Colored outer teeth = one per horizon hour when hours divides 144 (`24 → stride 6`) |
| `HorizonHourOptions` | 1…144 divisors | Allowed `ClockHorizonHours` values |
| `MinVisibleGearTipR` | 4 | Below this = assemble fail (speck) |
| Rim floor | 28 | Layouts clamp; catalog probes at 140 |

### 6.2 Catalog / progressions

1. Prefer `settings.ClockGearCompounds` if ≥2 live values and exact last sun.
2. Else legacy `ClockGearDriveMode` enum recipes (`TryGetProgression`).
3. **Catalog** (`PrecomputedCombos`): Nc from
   `{18,24,30,36,40,48,54,60,72,80,90,96}`, all **2–4** tuples (not 5), keep if
   exact last sun + `DensestStagePlanets` non-empty.
4. Labels look like: `3 rings · 3+2+1 planets · 6+60 / 6+36 / 6+24 · last 16T`.
5. `FormatCompounds` → `"6+24 / 6+18 / …"`.

**Legacy enum names lie:** modes like `Teeth144_96_64_32` now mean compound
recipes, not ring tooth ladders. UI writes `Concentric` + `ClockGearCompounds`.

### 6.3 Layout one stage (`LayoutPlanetaryStage` family)

1. Cut host (face `InternalAgainstRim` or previous band).
2. Planet pitch from `r_ring · Np / Nh`.
3. Carrier radius: tip/root clearances, floored.
4. Compound pitch from `Nc/Np`, **capped** vs carrier — may change
   `DrawCompoundTeeth` ≠ kinematic `CompoundTeeth`.
5. Idler: same pitch family as planet; mesh radius via `MeshCenters`
   (pitch sum **and** tip/root crosses).

### 6.4 Compound scale (`WithDrawnCompounds` / `MaxCompoundMul`)

- UI scale → integer `k` (`CompoundMulFromScale`).
- Grows **compound and idler** together at idler module (ratio preserved).
- Only draw/mesh fields change; last-stage RPM still uses stock Nc.
- Max k: still reaches sun, bodies clear, idlers clear (often UI max 8).
- After grow, idler offset re-snapped.

### 6.5 Seating

- **Planets:** snapped to nearest **ring valley** (not pure equal angles when
  `Nh/Np` isn’t integer). `PlanetAlignAngle` rolls a tooth into the valley.
- **Idler:** 0° = toward center; offset in signed degrees. Only **validated**
  seats (or fine scan when Ns locked). Free sliders get snapped.
- **One idler only** — `ClockGearIdlersPerCompound` forced to `1` on normalize;
  multi-idler pairs are not assemblable (kept for save compat).

### 6.6 Assembly validation (`FitInnerRing` / `ValidateAssembly`)

Fails when: tip too small, ring gap jams idler, pitch/radius spread too large,
tip↔valley phase fail (tol ~0.12 tooth), compound disk clash in **visible
annulus**, idler↔idler clash.

**Gotcha:** compound overlaps **outside** `[sunTip, rim]` are ignored (masked
lobes). Changing the mask without updating visible R min/max hides real clashes
or rejects fake ones.

### 6.7 `GearCutter`

ISO 53 involute, **20°**, addendum **1.0 m**, dedendum **1.25 m**,
`m = 2 r_pitch / z`. All clock outlines go through this cutter — one module
convention for the whole train. Low-z pinions get undercut radial runout below
the base circle. High tooth counts use fewer flank samples.

---

## 7. Overlay rendering (`ClockOverlay`)

### 7.1 Layer stack (XAML bottom → top)

1. `FaceFill` — attention / locked face disk  
2. `Gears` — mid-train (compounds, idlers, planets, hollow rings, sun)  
3. `Cues` — Discord / last-chance face overlays (above mid-train)  
4. `OuterDrive` — outer internal ring (plain teeth, under wedges)  
5. `Face` — ring stroke, wedges, hour “now” blade, hit plate  
6. `HourAccents` — colored hour-tooth accents (above wedges)  
7. `Hands` — second hand + hub  
8. `Marks` — time text, carets, debug labels  

Alerts sit above idlers/planets/hollow rings and below outer teeth and
hands — hence `Gears` / `Cues` / `OuterDrive`.

### 7.2 Refresh cadence

| Trigger | Behavior |
|---------|----------|
| `Refresh()` / resize / `SyncClockOverlay` | `RefreshFace(restageGears: true)` — rebuild gears |
| 1s tick | Clears Face/Marks/FaceFill; **keeps** gear scene graph |
| `CompositionTarget.Rendering` | Second hand + `UpdatePlanetaryDrive` every frame while gears (or second hand) on |

Horizon/`ringSpeed` are baked at **restage**. Changing horizon without a full
refresh can leave stale `secScale` until next restage.

### 7.3 Color resolution

```
ringHex     = ClockGearColor            default #7A8088
compoundHex = ClockGearCompoundColor    blank → Darken(ring, 0.55)
planetHex   = ClockGearPlanetColor      blank → ring          ← small compound pinion
idlerHex    = ClockGearIdlerColor       blank → ring
sunHex      = ClockGearSunColor         blank → Darken(ring, 0.72)
carriage    = ClockGearCarriageColor    default #C4A882 (opaque)
hour accents= ClockColorMajorTick       (outer every-12th teeth)
```

Config labels: **Compound large** vs **Compound small / planet**.

### 7.4 Opacity (`ClockGearOpacity` / `ClockGearRingOpacity`)

- **Dimmed** (`ClockGearOpacity`, default 0.42): carriage, compounds, planets, idlers.  
- **Intermediate hollow rings:** `ClockGearRingOpacity` applied on ring *paths*
  (stack canvas stays at Opacity 1 so planets/idlers parented there can use
  `ClockGearOpacity` without multiplying).  
- **Full strength:** outer internal ring + hour-tooth accents; **center sun**
  ring paths when that stage is the sun.

### 7.5 Paint order / compound masks (per stage) — critical

Comment in code: carriage + large compound → rings → idlers/planet → sun.

Compounds are masked with **tooth-shaped clip frames** (not tip-radius circles):

- Radial allow-band still includes the tooth valleys (root→root / rim→outer-root).
- Solid tooth/rim outlines are `Exclude`’d from that band; clip canvases spin with
  each ring so holes track the teeth — gaps stay open, solid teeth mask.
- Nested frames use relative `secScale` so compound inertial spin is unchanged.
- Planets/idlers keep a plain radial mesh clip so they can sit in the valleys.

Typical stage 0 paint order:

1. **Back carrier layer** (compound clip): carriage tracks → **compound** gears  
2. Outer internal ring + hour accents (full opacity, on gear root)  
3. Inner exterior crowns  
4. **Front carrier layer:** idlers → **planets** → carriage pins → hollow mask core  

Inner stages insert the compound back-layer with `behind: true` so z-order stays
under the host; the radius clip is what actually removes lobes under the rim.

Whole train clipped to the clock rim ellipse.

### 7.6 Hands relationship

- Gear drive **forces** hour + second hands on even if their checkboxes are off.  
- Hour hand is a **fixed “now” blade** (does not rotate).  
- Second hand tracks `SecondHandTurn` every frame.  
- With gears on, normal tick marks are skipped; outer hour teeth replace them.

### 7.7 Carriage (visual)

Per-stage `ClockGearCarriageStage`: Kind, Rail, Rung, SweepDeg, Reverse, Pins,
Hub, LinkRepeats, LinkStride.

| Kind | Look |
|------|------|
| Orbit | Twin rails + swept braces |
| PinLinks | Rails + bow compound pin → idler pin |
| Skeleton | Pin bows only |
| Cradles | Filled C-arms around pairs |
| Truss | X-lattice between rails |
| Helix | Helical ribbons |
| Fan | Fanned pin bows |

`LinkRepeats` copies the pin-link toward the next planet; `LinkStride` 0 = own
idler, 1+ = another planet’s idler (cross-set). Hidden entirely if
`ClockGearHideCarousel`.

### 7.8 Debug

- `ClockGearDebugLabels` — tooth counts, ratios, RPM×anim, spin diameters,
  carrier conflict / assemble fail text on `Marks`.  
- `ClockGearDebugRingCenter` — midpoint circles on solid rims.  

Debug marks clear every 1s face tick and only rebuild on gear restage — expect
labels to vanish between restages unless something calls `Refresh()`.

---

## 8. Settings reference (`AppSettings`)

Defaults and meaning (see `Models.cs` for XML docs):

| Property | Default | Notes |
|----------|---------|-------|
| `ClockGearDriveMode` | Off | Off / Concentric / legacy enums |
| `ClockGearCompounds` | `[]` | Live train Nc list |
| `ClockGearDebugLabels` | false | |
| `ClockGearDebugRingCenter` | false | |
| `ClockGearHideCarousel` | false | |
| `ClockGearAnimSpeed` | 1 | Gears only, 1–1440 |
| `ClockGearPlanetCounts` | `[3,3,3,3]` | Norm length 5, 1–12, assemblable ticks only in UI |
| `ClockGearPlanetOffsetsDeg` | zeros | Constellation rotation; **UI often Collapsed** but settings still apply |
| `ClockGearCarrierDegPerMin` | zeros | Manual/pinned °/min |
| `ClockGearAutoCarrier` | null→true | Equalize carousels; keep face rate + sun ~1 rpm |
| `ClockGearCarrierDirs` | Auto | Auto / CW / CCW / Still |
| `ClockGearCarrierMinDegPerMin` | 0 | Floor \|ω\| for unlocked auto rings |
| `ClockGearCarrierPinned` | [] | Lock ring to stored °/min under auto |
| `ClockGearCompoundScale` | `[1×5]` | Integer mul 1–24 |
| `ClockGearIdlersPerCompound` | 1 | **Forced 1** |
| `ClockGearIdlerOffsetsDeg` | zeros | Snapped to mesh |
| `ClockGearColor` / Compound / Planet / Idler / Sun / Carriage | see §7.3 | |
| `ClockGearCarriageStages` | [] | 5 after normalize; empty seeds from legacy scalars |
| `ClockGearOpacity` | 0.42 | |
| `ClockGearToothDepthFrac` / `WidthFrac` | 0.14 / 0.48 | Geometry; no aesthetics slider |

**Normalize** (`AppHost.NormalizeSettings`, gear block): pad lists to length 5,
force idlers=1, snap idler offsets, clear invalid optional colors, clamp opacity /
anim / scales. Runs at **load**, not on every `SyncClockOverlay` — live UI
clamps itself.

---

## 9. Config UI map

**Always visible (Aesthetics):**

- Drive mode / catalog combo → compounds + densest planets (Off clears)  
- Debug checkboxes  
- Color rows (ring, compound large, compound small/planet, idler, sun, carriage)  
- Per-ring carriage editors  
- Opacity %, anim speed  

**`ClockGearFiddlePanel`** (only when drive on and ≥2 compounds):

- Planet count sliders (assemblable ticks only)  
- Idler offset sliders (snapped)  
- Auto carousel + min °/min + hide frame  
- Carrier rate/dir/pin per ring + conflict warn text  
- Compound size sliders  

Pin a carrier by dragging its slider under auto; unpin via label click (see
code-behind). Carrier slider UI range is often −720…720 while normalize allows
±3600.

---

## 10. Sync / lifecycle

```
Config change → SyncClockOverlay() → ApplyChrome + Refresh(restage)
Save via RequestSave() as needed

Startup → load JSON → NormalizeSettings (incl. gear) → SyncClockOverlay
```

`ShowClockModule` (+ stay-when-hidden) gates overlay visibility.

---

## 11. Solved problems & gotchas (read before editing)

1. **Exact last sun or nothing** — trains without divisible ΠNc fail quietly.  
2. **Intermediate Ns cancel** — only last sun locks ×1440; planet count is
   cosmetic but must still mesh the same suns.  
3. **Draw ≠ kinematic teeth** — pitch caps and ×k; last sun uses stock Nc;
   mid stages use visual Nc.  
4. **One idler** — don’t reintroduce pairs without a new assemble path.  
5. **Valley snap ≠ equal angles** — multi-planet phase is harder than 360/N.  
6. **Idler offsets are discrete** — always snap after user input.  
7. **`MinVisibleGearTipR`** — tiny inner stages fail even if pitch math works.  
8. **Compound clash only in visible annulus** — mask and clash bounds must agree.  
9. **`behind: true` on inner stages** — z-order for compounds under host ring.  
10. **AnimSpeed ≠ hands**; **opacity exceptions** for outer teeth + sun.  
11. **Hands forced on** when gears on; hour blade does not spin.  
12. **Restage vs 1s tick** — don’t assume horizon changes rebuild gears.  
13. **Debug labels on Marks** flicker away until restage.  
14. **Catalog is 2–4 stages**; `ToothProgression` allows 5 and overlay loops ≥3,
    but catalog builder doesn’t emit 5-tuples.  
15. **Legacy enum / carriage scalars** — compounds + `CarriageStages` are
    source of truth; legacy fields seed empties only.  
16. **Carrier solver** — if every stage pinned, last pin is forcibly released so
    sun can still hit 1 rpm.  
17. **Hollow ring half-pitch offset** — interior vs exterior teeth keep rim
    thickness even; don’t “fix” that without redrawing masks.  
18. **Module shared when growing** — ×k at idler pitch; don’t change planet
    module independently without revisiting sun-reach formulas.

---

## 12. How to change common things

| Want | Where / how |
|------|-------------|
| New train in dropdown | Must pass `ExactLastSunTeeth` + `DensestStagePlanets`; extend catalog tooth set / tuples in `BuildCatalog` |
| Recolor small pinion | `ClockGearPlanetColor` (Compound small / planet) |
| Recolor large compound | `ClockGearCompoundColor` |
| Faster gears, same hands | `ClockGearAnimSpeed` |
| Hide rails only | `ClockGearHideCarousel` |
| Fatter compounds | Compound size slider → `ClockGearCompoundScale` (check `MaxCompoundMul`) |
| Different planet count | Only values from `ValidStagePlanetCounts` / completing sets |
| Fix “doesn’t assemble” | Check exact sun, tip radius, idler snap, clash annulus, rim ≥ 28 |
| Probe mul offline | `_probe/Program.cs` |
| Change involute shape | `GearCutter` only — keep one module convention |

---

## 13. Suggested workflow for future gear work

1. Read this doc + the glossary.  
2. Decide if the change is **kinematic** (geometry / Willis / catalog) or
   **visual** (paint, color, carriage, opacity).  
3. For kinematic changes: prove `ExactLastSunTeeth` and `ValidateAssembly` still
   pass at catalog rim and live rim; watch draw vs kinematic Nc.  
4. For visual changes: respect paint order and opacity exceptions.  
5. Normalize any new settings in `AppHost` gear normalize block.  
6. Publish with `build.ps1` → `dist` (settings live beside that exe).  
7. Update **this file** when you learn a new gotcha or change the contract.

---

## 14. Quick index (approx line ranges — search by symbol if drifted)

| Concern | Location |
|---------|----------|
| Constants, progressions, catalog | `ClockGearGeometry.cs` ~15–235 |
| Layout & compound mul | ~259–518 |
| Seating & idler enum | ~520–759 |
| Willis / carrier solve | ~789–1357 |
| Train assemble / ExactLastSun | ~1359–1651 |
| Fit / validate / Build* | ~1654–2248 |
| Involute cutter | `GearCutter.cs` (whole file) |
| `AddConcentricPlanetary` | `ClockOverlay.xaml.cs` ~898–1550 |
| CarrierFrameSpins / apply rot | ~1773–1798, ~2236–2247 |
| Color helpers | ~2526–2546 |
| Settings | `Models.cs` ~494–600, carriage ~754–832 |
| Normalize | `AppHost.cs` gear block ~3362–3414 |
| Sync overlay | `AppHost.SyncClockOverlay` ~1225–1253 |
| Config gear UI | `ConfigWindow.xaml` ~1159–1523 |
