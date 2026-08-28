# Architecture

Technical decisions and how the signature mechanics actually work. Read `README.md` first for
the stack and the milestone map.

---

## Networking

**FishNet, host-authoritative peer-to-peer over Steam Datagram Relay.**

One player is the host (client and server in the same process). Everyone else connects by SteamID
through the Steam transport, so there is no port forwarding, no dedicated server, and no monthly cost.
Development uses Steam **AppID 480 (Spacewar)** until the real app is purchased.

Tick rate 20–30Hz. Higher buys nothing here — the game is not competitive, and network jank on
ragdolls reads as comedy rather than as a defect.

### Who decides what

| System | Authority | Notes |
|---|---|---|
| Own movement, driven vehicle | Client owner | Predicted locally, reconciled by the server |
| Damage, HP, death | Host | Never trust a client-reported hit |
| Economy, inventory, shop | Host | All transactions validated server-side |
| RNG — roulette, loot, fishing | **Host, always** | Clients only animate a result already decided |
| AI (natives, animals) | Host | Simulated once, transforms replicated |
| Ragdoll, carry | Host simulates | Clients interpolate |

The rule: a client may *predict* anything about itself and may *animate* anything, but may never
*decide* anything that another player can observe or that touches the economy.

---

## The signature mechanics

### Stun and ragdoll

**v1 — kinematic switch (build this first).**
The character is an animated controller with a humanoid rig carrying a Rigidbody and Collider per
bone, kinematic by default. On stun or death the host sends a stun state; every client switches the
bones to non-kinematic, applies the impulse, and disables the controller. On recovery the ragdoll
pose is sampled and blended back into animation over a short window.

**v2 — active ragdoll (upgrade after M1 works).**
Every bone gets a `ConfigurableJoint` whose `slerpDrive` targets the current animation pose. A hit
temporarily drops `positionSpring`, so the character flops without going fully limp — still standing,
still trying, visibly failing. This is the trick behind Gang Beasts and Human Fall Flat, and it is
what separates "funny" from "a body fell over".

Do not attempt v2 before v1 ships. It is a tuning problem, and tuning is much easier once the
surrounding systems are stable.

### Carry

Interacting with a stunned or dead player asks the host to validate the grab. On approval the target's
hip Rigidbody goes kinematic and parents to the carrier's `CarrySocket`, and the two colliders are
mutually ignored via `Physics.IgnoreCollision`. Throwing applies an impulse to the hip and releases
the parent. Carrying slows the carrier, which is what makes hauling a corpse across the island a real
decision rather than a free action.

### Taser

Extended stun, plus random per-frame impulses on limbs while active, plus heavy camera shake on the
victim. Limited battery with recharge. Mechanically it is a longer punch; the jitter and the shake are
what make it worth carrying.

### Death and revive

At 0 HP the player object hands off to a networked `CorpseNetworkObject` that is carryable,
throwable, and loadable into vehicles, and which retains the dead player's inventory. Meanwhile the
player becomes a ghost that can spectate and lightly push physics objects — dead players stay
engaged, and stay able to interfere.

Revival means physically hauling the corpse to the Revive Machine at base and paying a cost that
scales with deaths this run. The whole loop is deliberately inconvenient: the inconvenience is the
content.

---

## World generation

The island is **generated from a seed, never hand-sculpted.** An editor script produces the heightmap
from domain-warped noise with an island falloff mask, then derives the splatmap from height and slope
rules (sand near sea level, grass inland, rock on steep slopes). Vegetation is placed by biome mask
using Terrain tree and detail instancing with LOD groups.

This matters for two reasons: the same seed reproduces the island byte-for-byte, and regenerating it
is a single terminal command. No manual editor work sits between an idea and a testable world.

Island 1 is 1024×1024 world units (~1 km²). Island 2 is 512×512 — smaller, denser, meaner.

Points of interest are placed by a `POISpawner` reading a config list. Adding a landmark is a data
edit, not an editor session.

---

## Data-driven content

**Every piece of content that is not geometry is a ScriptableObject.**

`WeaponDef`, `ItemDef`, `UpgradeDef`, `BuffDef`, `FishDef`, recipes, POI entries, shop inventories.
These serialise as YAML text, which means they are authorable from a terminal, reviewable in a diff,
and mergeable in git.

A new weapon is one `.asset` file plus a prefab. A new fish is one `.asset` file. This is the single
decision that lets content scale without the editor becoming a bottleneck.

---

## Vehicles

| Vehicle | Approach |
|---|---|
| Car | `WheelCollider` with deliberately arcade tuning — high grip, soft suspension, exaggerated mass. Easy to flip in a funny way |
| Boat | Custom buoyancy: 4–8 sample points applying Archimedes force plus drag against the water plane |
| Plane | Arcade flight model. Lift as a function of speed and angle of attack, forgiving stall. Learnable in minutes, landable with difficulty |

All vehicles share one framework: seat definitions, enter/exit interaction, passenger parenting, and
driver ownership transfer validated by the host. Vehicle-versus-ragdoll collisions launch players with
force proportional to impact speed — this is a required feature, not a side effect.

---

## Casino

The roulette wheel does not decide anything. The **host** rolls the result server-side and broadcasts
it; every client then animates the wheel to land on the number that was already chosen. There is no
code path where a client can influence an outcome.

Chips exist only inside the casino and are bought with in-game currency. **There is no path for real
money to enter or leave.** That is what keeps this legally not-gambling and keeps the app compliant —
Steam bans real-money gambling and paid loot boxes outright. The store page must still declare
gambling themes, alcohol references, and violence accurately.

The alcohol NPC trades drinks for buffs applied through the same `BuffDef` system as food and medical
items. The cost is a URP Volume override — depth of field, chromatic aberration, camera noise — that
genuinely impairs vision. The buff has to be tempting and the handicap has to be real, or the
mechanic is decoration.

---

## Performance

**Min spec is a Radeon 760M iGPU at 1080p60.** It is the development machine's integrated GPU, so it
is testable continuously rather than at the end.

Budget: URP forward rendering, SSAO off at Low, shadow distance ~80m with 2 cascades, aggressive LOD
groups, occlusion culling, distance fog to cut draw distance, baked lighting wherever it is possible.
The physics cost of 4 simultaneous ragdolls plus vehicles is the worst case to profile against.

Profile at every milestone, not at M8. Perf problems found late are architecture problems.

---

## Agent-assisted development

Built with Claude Code driving the terminal, via two MCP servers:

- **MCP for Unity** — editor control, scene and asset operations, console reading, test runs.
  **Requires the Unity Editor to be open**; there is no headless iteration loop beyond batchmode compiles.
- **blender-mcp** — low-poly modelling and CC0 assets from Poly Haven.

This is also why the engine choice went to Unity. Unity serialises scenes, prefabs, and
ScriptableObjects as YAML and its scripts are plain `.cs` files, so an agent can read, write, and diff
essentially the whole project from a terminal. UE5's Blueprints and `.uasset` files are binary, which
would have pushed the majority of the work back into manual editor sessions.

What the agent cannot do: playtesting, tuning game feel, and final art direction. Those stay human,
and M1 exists specifically to test the part no amount of code can decide.
