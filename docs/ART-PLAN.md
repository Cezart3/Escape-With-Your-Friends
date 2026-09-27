# Art plan: from programmer boxes to an artist's island

The M8 art pass (#76, #77, #78, #79), rewritten after a playtest verdict: carrying works, the look
does not. The trees look drawn in Paint, the people look like 1990. Everything on screen is
procedural geometry made by a programmer and painted with flat colours. The target is stylised
low poly that looks made by artists and modern, on **a Radeon 760M at 1080p and 60 fps on the
Medium tier**.

This file is the plan: what replaces what, why, under which licence, at what cost, in what order.
`docs/CREDITS.md` carries the licence table on its own. `docs/ARCHITECTURE.md` gets a section per
decision as each one lands.

---

## 0. The decisions, in eight lines

1. **Kenney is the principal author** for everything that does not move: nature, buildings,
   props, the casino, vehicles, weapons. It is the only catalogue whose every file could be opened,
   measured and looked at from here, and its current kits share one look.
2. **Quaternius supplies the people**: Universal Base Characters on the rig of the Universal
   Animation Library. Kenney's people are the problem being fixed (72 triangles, no skeleton).
3. **Nothing third-party is committed from the cloud.** You download the zips in a browser. An
   editor command (`ArtExtract`) copies only the files this plan names into
   `Assets/_Project/Art/ThirdParty/<Author>/<Pack>/`.
4. **Materials: one per Kenney kit** (its colormap texture), one per distinct flat colour for
   the few kits without one, one per character texture. The old `Palette` stays for gameplay
   primitives. `-lookTest` counts all of it, terrain trees included.
5. **Characters: the physics skeleton stays exactly as it is and becomes invisible.** A skinned
   model rides on top of it. When the player is upright an `Animator` plays real clips. When
   ragdolled, the model copies the physics bones. Carry, stun, ragdoll and every physics harness
   are untouched by construction.
6. **No HDRI.** The game has a 20-minute day cycle and a photograph is one frozen hour. The sky
   stays procedural. SSAO is added on Medium and High from `RenderTuning`. HDR goes on for Medium,
   because ACES and bloom do nothing without it.
7. **Budgets are numbers the harness enforces**: triangles per category, materials per scene,
   models standing up.
8. **Order of delivery:** nature, lighting and casino first (the most visible), then landmarks
   and stations, then characters, then animals, vehicles and weapons.

---

## 1. What the research could and could not see

Four Sonnet agents searched once, one category each. The sandbox **blocks** kenney.nl,
quaternius.com, poly.pizza, polyhaven.com, itch.io, opengameart.org, sketchfab.com, the Unity Asset
Store, mixamo.com and helpx.adobe.com. Only web search (snippets) and GitHub were reachable. So
every fact below carries one of three labels:

| Label | Means |
|---|---|
| **VERIFIED** | Opened here: a file, a licence text, a triangle count parsed out of the model, or a render of it. |
| **SNIPPET** | Seen only as a search-engine quote of the real page. |
| **LOCAL** | Could not be checked from here. You confirm it on the real page when you download. |

What was verified, and how:

- **Kenney.** Through `github.com/shorepine/kenney`, a CC0 mirror of Kenney's whole library as
  glTF-binary (49 kits, 4 812 models, pinned at commit `3694c68`). I parsed every model this plan
  names for triangles, materials, textures, skeleton and size, and rendered them to contact sheets
  to look at the shapes. The mirror carries Kenney's own `License.txt`: *"License: (Creative
  Commons Zero, CC0) … You can use this content for personal, educational, and commercial
  purposes. Support by crediting 'Kenney' or 'www.kenney.nl' (this is not a requirement)."*
- **Quaternius Universal Animation Library.** Through `github.com/J-Ponzo/gltf-universal-animation-library`,
  a re-upload of the free Standard edition "as distributed at itch.io on 2025-06-10", with the
  CC0 1.0 legal code as its LICENSE. Parsed: **46 clips**, one skin of **53 joints**
  (Rigify-style `DEF-hips`, `DEF-thigh.L`, `DEF-upper_arm.L` …), a demo mannequin of **13 743
  triangles** and 2 materials.
- **KayKit.** Official repositories under `github.com/KayKit-Game-Assets`, CC0 `LICENSE.txt`.
  Good characters, but no tropical nature, animals or vehicles. Not used; recorded because it was
  the third contender.
- **Quaternius everything else** (Universal Base Characters, Ultimate Nature, Pirate Kit,
  animals, guns): **SNIPPET only.** Every search result calls it CC0. No mirror with a licence
  file was found, and no file names or triangle counts could be checked.

**The honest consequence.** The licences of the Kenney kits and of UAL were read from a licence
file, but that file came through a mirror, not from the author's page. For Universal Base
Characters, even the licence is a snippet. The plan treats all of that as a **risk to close
locally**: when you download, open the zip's `License.txt`. `ArtExtract` copies it next to the
models and fails if a pack has none (§8, T1).

---

## 2. Principal author: Kenney. Quaternius for people only.

### Why Kenney, and not Quaternius

The brief expected Quaternius or Kenney. The research decided it on three counts:

1. **Coverage, verified file by file.** Kenney's current kits (Pirate, Survival, Watercraft, Car,
   Modular Cave, Holiday, 2024–2025) have palms, pines, rocks, logs, stumps, tropical plants, a
   thatched stilt hut, a canvas shelter, a log palisade, a rock cave mouth, a shipwreck, a campfire,
   chests, barrels, crates, bottles, lanterns, a string of coloured bulbs, tools, bar stools, a bar
   counter, a fishing boat, a speedboat and an SUV with separate wheels. Every one of those was
   parsed and rendered here. For Quaternius not one file could be checked.
2. **One texture per kit.** Every current Kenney kit paints every model from one 512×512
   `colormap.png` (VERIFIED: the 9 kits used here, 7–40 KB each). One material per kit is the best
   case for batching on an iGPU, and terrain trees from one kit share it.
3. **Cheap.** Palms 338–482 triangles, rocks 264–552, the shipwreck 2 282, bar stool 176
   (VERIFIED). A whole island of these costs less than one of today's jungle trees used to.

What Kenney does **not** have, and what fills it:

| Gap | Why Kenney fails | Filled by |
|---|---|---|
| People | `blocky-characters`: 72 triangles, no skeleton, node animation (VERIFIED). That is the 1990 look being fixed. | **Quaternius Universal Base Characters + Universal Animation Library** (§6). |
| Tropical broadleaf jungle | Kenney's broadleaf trees are park lollipops (rendered). | Kenney anyway (Platformer and Coaster round trees, plus bent palms), **flagged for your eye**. The swap is one table (§4). |
| Animals | `cube-pets` are literal cubes (VERIFIED: hog, deer, parrot, crab, fish). | Decided at P4 after you look: Quaternius animated animals (LOCAL), else cube-pets. |
| Light aircraft | No aircraft kit exists. | Keep today's procedural plane for now; candidates in §4. |
| Roulette wheel | Nobody free has one. | Kenney furniture table + felt + a **generated** wheel texture (§4, casino). |

### Why not the others

- **Quaternius as principal**: likely the better trees and certainly the better people, but none of
  it could be seen or measured. A plan whose main source is unverifiable is a plan built on
  guesses. It keeps the part only it can do.
- **KayKit**: best documented and best looking per asset, but no tropical nature, no animals, no
  vehicles.
- **Synty POLYGON (Tropical, Casino)**: exactly the look, but paid and under a restrictive EULA.
  Out of scope for a free pass; noted if you ever spend money on art.
- **Poly Haven HDRIs**: CC0 and beautiful, but see §7.
- **Mixamo**: allowed inside a compiled commercial game. Per Adobe's FAQ (SNIPPET): *"available
  for free, with no licensing or royalty fees, for unlimited commercial or non-commercial use"*.
  Raw files must not be redistributed (so never loose in Steam Workshop content). Download needs a
  browser and an Adobe account, and there is no API. Optional, only to fill the two clips UAL lacks
  (§6).

### Style risk, said plainly

Kenney's world is chunky and toy-like. Quaternius's people are smoother and closer to realistic
proportions. They may not sit together. That is a verdict only your eye can give (§10). The
fallback for the people is Quaternius *Ultimate Modular Men/Women*: flatter and more toy-like,
24 clips, its own rig. It swaps in behind the same `CharacterSkin` code.

---

## 3. Licences

Every pack below: **commercial, closed-source, sold on Steam: allowed.** None is NC, ND or
share-alike. CC0 needs no attribution. It is given anyway in `docs/CREDITS.md`, which is also the
provenance record.

| # | Pack | Author | Page | Licence | Evidence | Phase |
|---|---|---|---|---|---|---|
| K1 | Pirate Kit (72) | Kenney | https://kenney.nl/assets/pirate-kit | CC0 1.0 | VERIFIED via mirror `License.txt` | P1 |
| K2 | Survival Kit (80) | Kenney | https://kenney.nl/assets/survival-kit | CC0 1.0 | VERIFIED via mirror | P1 |
| K3 | Platformer Kit | Kenney | https://kenney.nl/assets/platformer-kit | CC0 1.0 | VERIFIED via mirror | P1 |
| K4 | Coaster Kit | Kenney | https://kenney.nl/assets/coaster-kit | CC0 1.0 | VERIFIED via mirror; page slug LOCAL | P1 |
| K5 | Castle Kit (2.0) | Kenney | https://kenney.nl/assets/castle-kit | CC0 1.0 | VERIFIED: its `License.txt` is the one quoted in §1 | P1 |
| K6 | Furniture Kit (140) | Kenney | https://kenney.nl/assets/furniture-kit | CC0 1.0 | VERIFIED via mirror | P1 |
| K7 | Holiday Kit (99) | Kenney | https://kenney.nl/assets/holiday-kit | CC0 1.0 | VERIFIED via mirror | P1 |
| K8 | Modular Cave Kit (40) | Kenney | https://kenney.nl/assets/modular-cave-kit | CC0 1.0 | VERIFIED via mirror | **dropped**: see the cave row in §4 |
| K9 | Watercraft Pack (46) | Kenney | https://kenney.nl/assets/watercraft-pack | CC0 1.0 | VERIFIED via mirror | P4 |
| K10 | Car Kit (50) | Kenney | https://kenney.nl/assets/car-kit | CC0 1.0 | VERIFIED via mirror | P4 |
| K11 | Weapon Pack | Kenney | https://kenney.nl/assets/weapon-pack | CC0 1.0 | VERIFIED via mirror; page slug LOCAL | P4 |
| Q1 | Universal Animation Library (Standard, free) | Quaternius | https://quaternius.com/packs/universalanimationlibrary.html | CC0 1.0 | VERIFIED: CC0 legal code in the itch re-upload | P3 |
| Q2 | Universal Base Characters (Standard, free) | Quaternius | https://quaternius.com/packs/universalbasecharacters.html | CC0 1.0 | **SNIPPET**: *"free to use in personal, educational and commercial projects"* | P3 |
| Q3 | Ultimate Animated Animal Pack / Farm Animals | Quaternius | https://quaternius.com (pick on the page) | CC0 | **SNIPPET**, species list unknown | P4, optional |

Rejected, with the reason:

| Candidate | Reason |
|---|---|
| Synty POLYGON Tropical / Casino | Paid; restrictive EULA. |
| Poly-by-Google models on Poly Pizza (e.g. the seagull) | Usually CC-BY. Acceptable in principle, but one-off authors break the look and the page could not be opened. |
| Sketchfab "Low Poly Plane" (scailman, CC-BY, parts separated) | Licence from a snippet only. Kept as the P4 plane candidate, not chosen. |
| OpenGameArt "Low-Poly Biplane" (mfep, CC0, 668 tris) | Same: SNIPPET only, one mesh, would need splitting. P4 candidate. |
| Cloudy-Crown skybox (GitHub) | No licence file in the repository. |
| Fantasy Skybox FREE / other Asset Store skies | Asset Store EULA and no scripted download; and no sky is needed (§7). |

---

## 4. Old → new

The IDs are the names `ArtCatalog` uses. **Kit/file** is the model's file name inside the Kenney
zip, minus the extension (the extractor takes the FBX). Target sizes are in metres. The importer
scales every model to its target, so a kit's own units never matter (Kenney's survival kit is
built on a 0.5-unit grid and its pirate kit on 2.5).

### Nature — `IslandFlora.Variants` → terrain tree prototypes (P1)

| Species | Old model (Blender, `flora.py`) | New ID ← Kit/file | Tris (VERIFIED) | Target |
|---|---|---|---|---|
| Palm | Palm_Tall, Palm_Short | `PalmStraight` ← pirate/palm-straight | 338 | 8.5 m tall |
| | | `PalmBend` ← pirate/palm-bend | 338 | 7.5 m |
| | | `PalmTall` ← pirate/palm-detailed-straight | 482 | 9.5 m |
| | | `PalmLean` ← pirate/palm-detailed-bend | 482 | 8.5 m |
| JungleTree | Tree_Large, Tree_Mid, Tree_Small, Tree_Dead | `JungleRound` ← platformer/tree | 408 | 9 m |
| | | `JungleTall` ← coaster/tree-large | 284 | 10 m |
| | | `JungleSmall` ← coaster/tree | 284 | 6.5 m |
| | | `JunglePalm` ← pirate/palm-detailed-bend | 482 | 8 m |
| HighlandTree | Pine_Tall, Pine_Mid | `Pine` ← survival/tree | 226 | 9 m |
| | | `PineTall` ← survival/tree-tall | 254 | 12 m |
| | | `PineWide` ← castle/tree-large | 229 | 10 m |
| | | `PineSmall` ← castle/tree-small | 164 | 6 m |
| Bush | Bush_Wide, Bush_Small, Bush_Berry, Fern | `Leafy` ← pirate/grass-plant | 144 | 1.4 m |
| | | `Frond` ← platformer/plant | 108 | 1.1 m |
| | | `Grass` ← survival/grass-large | 144 | 0.8 m |
| | | `Flowers` ← platformer/flowers (a flat patch) | 264 | 1.2 m wide |
| Ground | Rock_Mid, Rock_Small, Rock_Large, Log, Stump | `Rocks` ← pirate/rocks-a | 264 | 2.4 m wide |
| | | `RocksSmall` ← pirate/rocks-b | 300 | 1.4 m wide |
| | | `RocksSand` ← pirate/rocks-sand-a | 552 | 3.2 m wide |
| | | `Log` ← survival/tree-log | 88 | 3 m long |
| | | `Stump` ← survival/tree-trunk | 52 | 0.8 m tall |

Placement rules, densities and the grove masks stay exactly as they are. Only the variant list
changes; the prototype count goes from 17 to 21, which the bake absorbs because it rewrites
every tree instance anyway. Grass stays a detail billboard.

**Your eye decides the jungle.** If the round trees read as a park, the swap is Quaternius
*Ultimate Nature Pack* `CommonTree_1..5` (LOCAL names; flat-coloured; `ArtCatalog` handles
flat-colour packs, §5). It is a table edit and a re-bake.

### Landmarks — `GreyboxBuilder` (casino P1, the rest P2)

The greybox **layout stays authoritative**. Every named box keeps its name, its collider and
its place, so `-casinoTest`, the POI validation and the NavMesh bake see what they saw before.
Only its `MeshRenderer` is switched off, and an art model is hung under it and fitted to it.
There are two helpers (`ArtDress`, §8 T5):

- **Fit** stretches one model over a box. Right for a roof, a counter, a hull, a rock.
- **Tile** repeats a module along a box's length at the box's height. Right for walls and
  floors, which would look smeared if stretched.

| Landmark | Greybox piece(s) | New | How |
|---|---|---|---|
| **Casino** "The Shack" | Floor (+5 planks) | survival/floor, survival/floor-old | Tile, alternating |
| | Wall.Back/Left/Right/FrontLeft/FrontRight, Door.Lintel | survival/floor stood upright (a plank panel) | Tile |
| | Roof (N/S slabs + ridge) | pirate/structure-roof (thatched, VERIFIED 348 tris) | Fit over the whole footprint; its corner posts land in the wall corners |
| | Bar, Bar.Front | furniture/kitchenBar ×3 | Tile |
| | Bar.Bottle0..5 | pirate/bottle | Fit, one each |
| | Stool0..3 (+cushions) | furniture/stoolBar | Fit (replaces the crates) |
| | Chandelier.Line / .Bottle | holiday/lights-colored (a string of coloured bulbs, 760 tris) | Tile along the line |
| | Sign / Sign.Letters | survival/floor-old board, keeps the crooked angle | Fit |
| | Window.L/R (dark recess) | unchanged | |
| | extra decor | pirate/crate-bottles by the bar, holiday/lantern by the door | New `Decor.*` children, no collider |
| | Lamp.* lights, `TackyLights` | unchanged | |
| **RouletteTable** (`CasinoFactory`) | Baize, Rim | furniture/table stretched + existing felt top (`Felt`) | Fit |
| | Wheel (+Zero marker) | cylinder kept, painted with a **generated** 37-pocket texture (`Art/Casino/RouletteWheel.png`) | The `Zero` marker the harness reads stays |
| | bet spots | unchanged (gameplay objects) | |
| **BaseCamp** | Shelter.Roof + 4 posts | survival/structure-canvas | Fit |
| | Shelter.Mat0/1 | survival/bedroll | Fit |
| | Storage (+Lid) | survival/box-large | Fit |
| | Bench (+legs, vice) | survival/workbench | Fit |
| | Fire.Ring / Fire.Logs | survival/campfire-pit | Fit |
| **Shop** | Hut | survival/floor planks on the back and sides, open at the front (the trader stands inside the hut's box) | Tile |
| | Roof | pirate/structure-roof | Fit |
| | Counter, Counter.Front | furniture/kitchenBar ×3 | Tile |
| | Stock0..n | survival/barrel, box, bottle (cycled) | Fit |
| | Sign | survival/signpost | Fit |
| **NativeVillage** | Hut0..4 | survival/fence-fortified palisade on all four faces. Not the pirate stilt frame: it is see-through, and the hut is a solid box to the physics | Tile |
| | Hut0..4.Roof | pirate/structure-roof | Fit |
| | Totem (+arms) | survival/tree-trunk ×3, stacked | Tile, vertical |
| | Fire | survival/campfire-stand | Fit |
| | — (new) | survival/fence-fortified palisade on the village edge | Decor, P2 |
| **Wreck** | Hull, Deck | pirate/ship-wreck (2 282 tris) | Fitted by footprint under the hull, keeping its 28° list; taller than the box because it has masts |
| | Mast (fallen) | survival/tree-log | Fit |
| | Debris0..3 | pirate/crate, barrel, boat-row-small | Fit |
| **Cave** | Rock.Left/Right/Lintel/Back | pirate/rocks-a | Fit, one each. Not modular-cave/gate-rock: its arch opening, stretched over the mouth, would be twice the 3 m gap the colliders leave |
| | Room.*, Ore | unchanged | It is dark in there |

### Stations and props — P2

| Prefab (builder) | New | Note |
|---|---|---|
| StorageChest (`StorageBuilder`) | survival/chest | Has open/close clips; wiring them is a later nicety |
| CraftingBench (`StationBuilder`) | survival/workbench | |
| Campfire (`StationBuilder`) | survival/campfire-pit | Light and heat components untouched |
| WaterFilter (`StationBuilder`) | survival/barrel-open + survival/bucket | |
| ShopCounter (`ShopFactory`) | furniture/kitchenBar | |
| ChipWindow / CashWindow (`CasinoFactory`) | survival/structure + survival/signpost | The sign colour still tells them apart |
| ReviveMachine | unchanged | A sci-fi joke machine; nothing free fits it better than the joke |
| HangPoint, WorldItem | unchanged | Gameplay markers |

These factories only build when the prefab is missing, and a deleted prefab loses its GUID and
unhooks every scene. So dressing is done **in place** with `PrefabUtility.LoadPrefabContents` →
dress → `SaveAsPrefabAsset` on the same path, which keeps the GUID.

### People — P3

| Prefab (builder) | New |
|---|---|
| Player (`PlayerPrefabBuilder`) | 4 variants of Quaternius Universal Base Characters, one per player slot, on a `CharacterSkin` (§6) |
| Native ×4 roles (`NativeFactory`) | Same bodies, tribal colour variant and a spear/blowgun prop; same controller |
| Castaway (`CastawayBuilder`), Barman (`CasinoFactory`) | One base character each, idle/sit clips |

### Animals, vehicles, weapons — P4

| Old (builder) | New | Note |
|---|---|---|
| boar, deer, gull (`AnimalFactory`) | Quaternius animated animals if the species exist (LOCAL); else Kenney cube-pets `animal-hog`, `animal-deer`, `animal-parrot` | Your eye picks |
| Buggy (`VehicleBuilder`) | car/suv (2 474 tris) + car/wheel-default ×4 (332 each) | Wheels separate (VERIFIED separate models) |
| Boat (`BoatBuilder`) | watercraft/boat-fishing-small (237) or boat-speed-a (156) | |
| Plane + parts (`PlaneBuilder`, `PlanePartBuilder`) | **unchanged for now** | Candidates: mfep biplane (CC0, SNIPPET, split in Blender) or scailman plane (CC-BY, parts separated, SNIPPET). Decide after you look |
| pistol, rifle, shotgun, smg, knife (`WeaponFactory`) | weapon/pistol (350), weapon/sniper or machinegun (486), weapon/shotgun, weapon/uzi, weapon/knife_sharp | Flat-colour materials (grey, greyDark, dark, wood) |
| hatchet, shovel | survival/tool-axe, survival/tool-shovel | |
| machete, bat, chainsaw, fists | machete ← knife_sharp stretched; bat ← survival/tree-log-small; chainsaw unchanged | Gaps, said so |

---

## 5. Materials

**Decision: adopt each kit's own atlas. Do not remap onto `Palette`.**

Remapping an artist's colours onto fifteen programmer colours is exactly how the island got its
"painted in Paint" look. The palette was right for greybox and wrong for art. So:

| Source | Material | Count |
|---|---|---|
| Kenney kits with `colormap.png` (all P1–P2 kits except furniture) | One `Kenney_<Kit>.mat` per kit: URP/Lit, `_BaseMap` = the kit's colormap, smoothness 0.1, instancing on. Every slot named `colormap` in that kit's FBX is remapped to it. | 6 in P1–P2 (pirate, survival, platformer, coaster, castle, holiday) |
| Kenney flat-colour kits (furniture, weapon) | One `Flat_<RRGGBB>.mat` per **distinct colour** across the whole ThirdParty folder, deduplicated. Same `AddRemap` mechanism `ModelLibrary` already uses. | ≈6–10 |
| Quaternius characters | One per character texture, shared by all four variants where the texture is shared | ≤ 4 |
| `Palette` (Greybox) | Stays, for what is still a primitive: bet spots, markers, the revive machine, the tracer, UI-facing pieces | ≤ 15 |
| World (terrain layers, water, sky, grass) | unchanged | ≈ 6 |

Texture import for colormaps: max 512, no mipmaps (a mip of a swatch atlas bleeds neighbouring
colours onto distant models), bilinear, no compression artefacts (uncompressed. They are 7–40 KB).

**What happens to `-lookTest`.** Its budget of 40 materials was a ratchet for the greybox. It
becomes **48**, and the count now includes the materials on **terrain tree prototypes**, which
it never saw: the terrain draws trees without `Renderer` components, so a forest of stray
materials was invisible to it. It prints the count per folder (`Greybox`, `ThirdParty`, other), so
a jump says where it came from. It keeps failing on missing shaders, blank renderers and
uninstanced repeats. It gains the triangle and "lying down" checks (§8). After the first real run
the budget is ratcheted down to the measured count plus four.

---

## 6. Characters — the risky part

### What exists

`PlayerPrefabBuilder` builds a physics skeleton of 11 bones (`Hips`, `Chest`, `Head`,
`UpperArm.L/R`, `LowerArm.L/R`, `UpperLeg.L/R`, `LowerLeg.L/R`). Each is a `Rigidbody`, a
primitive mesh that doubles as its collider, and a `CharacterJoint`. `RagdollController` flips
kinematic. `BodyAnimator` writes procedural rotations onto the kinematic bones. The camera rides
`Head`, carry hangs bodies off `CarrySocket`, impacts push rigidbodies. The rest pose is a T-pose
(arms along ±X).

### The options

- **(a) Build the ragdoll on the imported skeleton.** Bones, masses, joint axes and collider sizes
  all change to the model's. Every physics number that `-impactTest`, `-carryTest`,
  `-reviveTest` and the feel were tuned on changes with them, and the model's bone axes are
  whatever Blender left. Maximum risk.
- **(b) Copy the ragdoll onto the model in `LateUpdate`.** Physics untouched. Alone, though, the
  standing body would still be animated by `BodyAnimator`'s sine wave, which is not what makes a
  person look modern.
- **(c) Something simpler.** Nothing simpler keeps both the physics and real animation.

### Decision: (b), plus an `Animator` while upright

`CharacterSkin` (runtime, `Scripts/Player/`) owns a Quaternius body under the player root:

1. **The physics skeleton is kept exactly.** Same bones, colliders, joints and masses. Only the
   primitives' `MeshRenderer`s are switched off at build time. The colliders are the same
   components, so nothing physical can change. `BodyAnimator` keeps posing the kinematic bones, so
   `-animTest` is unchanged.
2. **Upright:** the body's `Animator` plays UAL clips from a controller that
   `CharacterAnimatorBuilder` generates (no hand-made `.controller`). Its inputs are measured the
   way `BodyAnimator` already measures them, locally, with no network traffic: speed from distance
   travelled, airborne from vertical speed, `CarrySystem.IsCarrying`, `VehicleRider` seated, and
   a punch trigger.
3. **Ragdolled:** the `Animator` stops. In `LateUpdate`, after `BodyAnimator`, each of the 11
   physics bones drives its humanoid counterpart:
   `model.rotation = physics.rotation * offset[i]`, with hips position too. The offsets are
   computed once at bind time by **aligning limb directions**, not by copying deltas. The ragdoll
   rests in a T-pose and the model in its bind pose (T or A), and copying deltas between different
   rest poses puts a limp arm through the chest. Unmapped bones (spine, neck, hands, fingers) keep
   their local pose and follow.
4. **Getting up:** the `Animator` resumes, and for 0.35 s each mapped bone is slerped from the last
   ragdoll pose to the animated one. There is no get-up clip in UAL (see below).
5. **Bone lookup through the Humanoid avatar**, `Animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg)`,
   never by name. The UAL rig is Rigify-named (`DEF-thigh.L`, VERIFIED), and a name table would
   break on the first differently exported body.
6. **Headless servers never build it.** With no graphics device the skin is destroyed in `Awake`,
   so the host's CPU does not animate four people nobody on it can see. The harness flag keeps it.
7. **Four players, four bodies.** Variant chosen by `ObjectId % 4`, the same rule
   `BodyAnimator.Paint` uses today, so every peer agrees without traffic. `PlayerIdentity`'s tint
   is pointed at one small accessory rather than the whole textured body. Tinting a texture by
   the player colour would dye the skin.
8. **The owner's own body** renders `ShadowsOnly`, the same rule as the primitives today, so you
   see your shadow and not the inside of your head.

**UAL clips mapped (all VERIFIED names):** `Idle_Loop`, `Walk_Loop`, `Jog_Fwd_Loop`,
`Sprint_Loop` (a speed blend), `Jump_Start` / `Jump_Loop` / `Jump_Land`, `Punch_Jab`,
`Punch_Cross`, `Driving_Loop` (seated in a vehicle), `Swim_Fwd_Loop` / `Swim_Idle_Loop` (if the
water ever asks), `Hit_Chest` / `Hit_Head` (stun), `PickUp_Table` (picking an item up),
`Sitting_Idle_Loop` (castaway, barman).

**Gaps, said directly:**

- **No carry clip.** Carry uses an upper-body layer (an `AvatarMask`) holding `Driving_Loop`'s
  arms-forward pose. It reads as holding something in front. Whether it reads as carrying a
  person is your call.
- **No get-up clip.** The 0.35 s pose blend stands in. Mixamo's *Getting Up* and *Carrying* would
  fill both, allowed in the build (§2), via your browser. Optional, P5.
- **Death:** a dead body already ragdolls, so `Death01` is not needed.

**Harness: `-skinTest`** (solo, headless, forces the skin on):

- every mapped bone resolves through the avatar;
- LOD0 ≤ 15 000 triangles, ≤ 2 materials;
- dragging the body makes the `Animator` speed rise and the model's thigh move;
- ragdolled, every mapped limb points within 20° of its physics bone and the hips within 0.15 m;
- after getting up the model stops tracking the physics bones;
- carrying sets the carry layer's weight;
- a headless host without the flag has no skin at all.

`-animTest`, `-impactTest`, `-carryTest 1`, `-reviveTest 1` and `-deathTest` run unchanged, as
the proof that physics did not move.

---

## 7. Lighting

**Sky: stays procedural. No HDRI.** Three reasons, in order:

1. The game has a full day/night cycle (`DayNightCycle`, 20 minutes). A photographic HDRI is one
   frozen hour. Two HDRIs blended are still two frozen hours and a shader to write.
2. A photographic sky over flat-shaded models is the textbook low-poly mismatch.
3. On an iGPU a 2K equirect sampled every frame is fill-rate spent on the one pixel region that is
   already cheap.

What changes instead:

- **Ambient** stays `Trilight`, driven by the profile's three gradients. That is already the
  cheapest correct ambient for a moving sun. Retuning its colours against the new albedos is a
  feel pass for you with `-timeOfDay` (§10), not a number to guess from here.
- **SSAO on Medium and High**, generated in `RenderTuning.Apply` into `URP_Medium_Renderer` and
  `URP_High_Renderer`, never on Low. Settings go through `SerializedObject` on `m_Settings.*`,
  because the feature class is public but its settings class is `internal` in URP 17.3
  (VERIFIED against `Unity-Technologies/Graphics@6000.3/staging`):

  | | Medium (the 760M target) | High |
  |---|---|---|
  | `AfterOpaque` | **true**: a multiply over the lit image, no depth prepass | false |
  | `Source` | Depth | DepthNormals |
  | `Downsample` | true (half resolution) | false |
  | `Samples` | Low (4) | Medium (8) |
  | `BlurQuality` | Low (Kawase) | Medium (Gaussian) |
  | `Falloff` | 50 m | 100 m |
  | `Intensity`, `Radius` | URP defaults (3.0, 0.035) | same |

- **HDR on for Medium.** Today it is off on Low and Medium. That means ACES has nothing above 1.0
  to roll off, and a bloom with threshold 1.05 never fires. URP 17 renders HDR into R11G11B10,
  32 bits a pixel, the same bandwidth as LDR. Low stays LDR.
- **Post-processing actually reaches the camera.** `PostProcess.Begin` builds the global volume,
  but URP ignores volumes unless the camera sets `renderPostProcessing`. Only `DrunkVision` sets
  it, so the grade appeared when a player got drunk. `PostProcess` switches it on for the main
  camera on every scene load. That is a bug fix, found while reading.

---

## 8. Performance budget

Target: **Radeon 760M, 1080p, 60 fps, Medium tier**: 16.6 ms a frame. `GraphicsBoot` still
starts an iGPU on **Low**. Medium is what it must hold when you choose it (#83 measures it).

### Triangles, LOD0, per prefab

| Category | Cap | Verified examples |
|---|---|---|
| Palm / tree | 600 | palms 338–482, platformer tree 408, pines 226–254 |
| Bush / plant / flower | 300 | 108–264 |
| Rock cluster | 600 | 264–552 |
| Log / stump | 150 | 52–88 |
| Small prop (bottle, tool, stool, lantern) | 400 | 74–412 |
| Medium prop (chest, barrel, crate, bench, campfire, table) | 800 | 236–572 |
| Landmark, all dressing summed | 8 000 | casino ≈ 4–6 k estimated from tile counts |
| Shipwreck | 2 500 | 2 282 |
| Vehicle, body + wheels | 4 000 | SUV 2 474 + 4 × 332 |
| Character, LOD0 | 15 000 | UAL mannequin 13 743 |
| Animal | 1 500 | cube-pets 422–676 |
| Held weapon | 2 000 | 84–1 660 |

**Per frame, Medium, busiest view (village or casino at dusk):** ≤ 1.2 M triangles in the camera
pass, ≤ 0.6 M in the shadow pass, ≤ 1 500 batches with the SRP Batcher on.

### Materials

≤ 48 distinct per scene (`-lookTest`), every one instanced. Measured breakdown after the first
bake, then ratcheted.

### LOD and culling

- **Terrain trees:** the two-level `LODGroup` stays: LOD0 casts shadows, LOD1 the same mesh
  without. Switch at 22 % screen height, culled at 2 %. Tree draw distance per tier stays in
  `TerrainQuality` (320 m × tier factor).
- **Props:** a single-level `LODGroup` that culls at 1.5 % screen height, so a bottle 60 m away
  is gone. Nothing smaller than 0.5 m casts a shadow.
- **Landmark shells:** culled at 0.5 %. Shells cast shadows; decor does not (the greybox rule
  already).
- **Characters:** no LOD mesh exists and none is invented. `Animator.cullingMode =
  CullUpdateTransforms`, so off-screen natives stop animating. Worst case: 4 players plus
  12 natives ≈ 220 k skinned triangles.
- **Textures:** colormaps 512² uncompressed, character textures ≤ 1024². No normal maps
  anywhere; if a pack ships them, they are not extracted.

---

## 9. Implementation, in tasks

Each task names its files, what it does, and the log line that proves it. **Opus** marks design
C#. **Sonnet** marks work concrete enough to delegate: Sonnet writes to the spec, Opus reviews
every Sonnet diff before it is committed. Nothing here edits a `.unity`, `.prefab` or `.asset` by
hand.

**Status after the first session:** T1–T6 written, plus T7 (all six landmarks). Nothing is
compiled or run: the build machine has no Unity and could not download a kit. T8 onward is not
started.

### P1 — the most visible: nature, lighting, casino

**T0 · You, locally · download.** Browser, into `D:\Downloads\ewyf-art\`: K1–K7. That is
all of P1 and the landmarks of P2.
Any zip name works; the extractor matches on the kit name. Open each `License.txt` and check it
says CC0.

**T1 · Opus · the pipeline.** New `Editor/ArtCatalog.cs` (packs + model table: ID, pack,
file, category, target size, upright), `Editor/ArtExtract.cs` (batchmode `-artZips <dir>`:
copies only catalogued files plus colormaps and `License.txt` into `Art/ThirdParty/Kenney/<Kit>/`;
fails listing anything missing, with the zip's nearest file names), and `Editor/ArtLibrary.cs`,
which replaces `ModelLibrary`: import settings, material remap per §5, scale-to-target, the
two-level LOD, the collider rule, and an **upright self-check**. An upright model that imports
lying down is re-imported with the other `bakeAxisConversion` setting; if it still lies, that is
an error naming the pack. Deletes `ModelLibrary.cs`, `tools/blender/flora.py` and the 18 FBX and
prefabs in `Art/Models`.
*Done:* `ArtExtract` logs `0 missing`; `ArtLibrary.BuildAll` logs one line per model with
triangles, size and `upright`, and no errors.

**T2 · Opus · flora switch.** `World/IslandFlora.cs` (`FloraModel` gains a target size; the
variant table above), `Editor/FloraFactory.cs` (calls `ArtLibrary`).
*Done:* both bakes log `Scattered N plants` and the triangle budget line with no category over its
cap.

**T3 · Opus · the look harness.** `World/LookTest.cs` extended; new runtime marker
`World/ArtVisual.cs` (ID, category, upright, and the caps table from §8, shared by editor and
harness). New checks: terrain prototypes' materials counted; each prototype and each `ArtVisual`
under its triangle cap; every upright one standing (height ≥ 0.8 × its largest horizontal extent);
per-folder material breakdown printed.
*Done:* `-lookTest` on both islands prints `N passed, 0 failed`.

**T4 · Opus · lighting.** `Editor/RenderTuning.cs` (SSAO on Medium and High renderers, Medium
HDR), `World/PostProcess.cs` (camera flag on every scene load).
*Done:* `RenderTuning.Apply` logs `SSAO on URP_Medium_Renderer (after opaque, depth, half res, 4
samples)`, the same for High, `none` for Low; the build is clean.

**T5 · Opus · casino.** New `Editor/ArtDress.cs` (`Fit`, `Tile`, `DressPrefab` in place),
`Editor/GreyboxBuilder.cs` (`BuildCasino` dressed per §4), `Editor/CasinoFactory.cs` (table
dressed; wheel texture generated into `Art/Casino/RouletteWheel.png`).
*Done:* `GreyboxBuilder.BuildAll` clean; `-casinoTest`, `-rouletteTest`, `-chipsTest`,
`-drunkTest` all `0 failed`; `-lookTest` still `0 failed`.

**T6 · Opus · docs.** An `ARCHITECTURE.md` section per landed decision; the Blender draft
rewritten as "third-party models, and the axis they arrive in"; `CREDITS.md` updated.

### P2 — the rest of the world

**T7 · Sonnet, spec §4 · landmarks.** `GreyboxBuilder` BaseCamp, Shop, NativeVillage, Wreck, Cave
dressed with `ArtDress` exactly per the table. *Done:* `BuildAll` clean, `-lookTest`,
`-economyTest`, `-prisonTest`, `-nativeTest` (solo, without `-noNatives`) `0 failed`.

**T8 · Sonnet, spec §4 · stations.** `StorageBuilder`, `StationBuilder`, `ShopFactory`,
`CasinoFactory` booths dressed in place with `ArtDress.DressPrefab`. *Done:* each builder
logs the GUID unchanged; `-chestTest`, `-craftTest`, `-shopTest` `0 failed`.

### P3 — people

**T9 · Opus · `CharacterSkin`.** `Player/CharacterSkin.cs`, `Editor/CharacterAnimatorBuilder.cs`,
`Editor/PlayerPrefabBuilder.cs` (skins under the root, primitives' renderers off, identity tint
re-pointed), `Player/SkinTest.cs` and its line in `NetworkBootstrap`. Humanoid import for Q1/Q2
in `ArtLibrary`. *Done:* `-skinTest` `0 failed`; `-animTest`, `-impactTest`, `-carryTest 1`,
`-reviveTest 1`, `-deathTest` unchanged.

**T10 · Opus, then Sonnet · natives and NPCs.** `NativeFactory`, `CastawayBuilder`,
`CasinoFactory.Barman` on the same skins. *Done:* `-nativeTest` (solo, without `-noNatives`),
`-rescueTest`, `-casinoTest` `0 failed`.

### P4 — animals, vehicles, weapons (after your eye on P1–P3)

**T11 · Sonnet · vehicles.** `VehicleBuilder` (SUV + 4 wheels mapped onto the existing wheel
transforms), `BoatBuilder`. *Done:* `-carTest`, `-boatTest`, `-vehicleTest` `0 failed`.
**T12 · Sonnet · weapons.** `WeaponFactory` models per §4. *Done:* `-weaponTest`, `-gunTest`,
`-meleeTest` `0 failed`.
**T13 · Sonnet · animals**, after you pick the pack. *Done:* `-animalTest` `0 failed`.
**T14 · Opus · plane**, after you pick the model.

### P5 — optional

Mixamo *Carrying* and *Getting Up* through your browser, dropped into the same controller.

---

## 10. What only you can judge

- Whether the island now looks made by artists, or just differently programmed.
- Whether Kenney's round trees make a jungle or a park (the swap is §4).
- Whether Quaternius people fit a Kenney world, and which four bodies are the four players.
- The carry pose and the 0.35 s get-up: funny or broken.
- Ambient colours against the new albedos at dawn, noon, dusk and night
  (`-timeOfDay 0.28 / 0.5 / 0.72 / 0.9`).
- 60 fps on the 760M at Medium, measured (#83).
