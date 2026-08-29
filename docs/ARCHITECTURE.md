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

### Scene flow and startup

`Assets/_Project/Scenes/Bootstrap.unity` is build index 0 and holds nothing gameplay-specific: a
camera, a light, a placeholder floor, and the `NetworkManager`. Gameplay scenes load and unload around
it, so the connection survives travelling between the island, the boat and the second island.

The `NetworkManager` object carries four components. FishNet creates the rest of its sub-managers
itself in `Awake`, so only these are worth pinning down in the scene:

| Component | Why it is set explicitly |
|---|---|
| `NetworkManager` | Holds `DefaultPrefabObjects`. FishNet can find it by scanning the project, but assigning it writes the reference into the scene where a diff can see it |
| `TimeManager` | Tick rate 30. FishNet already defaults to 30; leaving it implicit means a package update could quietly change how fast the whole game simulates |
| `Tugboat` | Plain UDP on port 7770, 8 clients. Placeholder — shipping runs on the Steam transport (#13) |
| `NetworkBootstrap` | Starts the connection from command-line arguments |

**Why Tugboat when shipping is Steam.** The Steam transport needs a running Steam client and an app
id, which makes it useless headless. Tugboat needs neither, so four instances can be launched from one
script. Both are FishNet transports behind the same interface, so nothing above this layer knows which
one is loaded.

**Why a command-line bootstrap.** The whole development loop for this project is a terminal, and a
build that can only be started by clicking a button cannot be tested from one. `NetworkBootstrap`
reads `-host` / `-server` / `-client`, plus `-address`, `-port` and `-quitAfter`:

```
Unity.exe -quit -batchmode -nographics -projectPath .   -executeMethod EscapeWithYourFriends.EditorTools.BuildTool.PerformBuild   -buildOutput D:/Builds/EWYF-dev -development -scriptingBackend mono

EscapeWithYourFriends.exe -batchmode -nographics -host   -port 7770 -quitAfter 18 -logfile host.log
EscapeWithYourFriends.exe -batchmode -nographics -client -address 127.0.0.1 -port 7770 -logfile c1.log
```

With no arguments it does nothing and waits for the lobby, which is what a shipped build does. In the
editor it auto-hosts, so pressing play is a one-player session rather than a disconnected one.

`-scriptingBackend mono` builds in about a minute against roughly ten for IL2CPP, which is the
difference between a smoke test that gets run and one that does not. The backend is a serialized
project setting, so `BuildTool` restores whatever was there before — a fast test build must not
quietly change what a release ships with.

### Player bodies, identity and the registry

A connection is not a player. `PlayerSpawner` on the `NetworkManager` object turns one into the other:
it listens to `SceneManager.OnClientLoadedStartScenes`, not to the connection state, because a
connection that has not finished loading the start scenes has nowhere to put a body yet. The host is a
client of itself and arrives through the same event, so it needs no special case.

Each spawn does four things, in order:

1. `GetPooledInstantiated` + `ServerManager.Spawn(body, connection)` — the connection owns its body.
2. `SceneManager.AddOwnerToDefaultScene(body)` — without it the body stays in the spawner's scene and
   the owning client, which loaded its own copy of the start scenes, never becomes an observer of it.
3. A spawn point if any are assigned, otherwise a generated 8-slot ring facing the middle. A greybox
   scene with nothing but a floor still spawns four players who can see each other.
4. `PlayerIdentity.ServerSetIdentity(name, colourSlot)`.

**Colour is a palette index, not an RGB value.** One replicated byte instead of sixteen, and it
guarantees the set of player colours stays the set that was chosen to be distinguishable — including
through the blur the alcohol buff puts over the camera. The server hands out the lowest free slot and
frees it on disconnect, so with four players the four most distinct colours are always the ones in
play. More players than palette entries wraps rather than refusing to spawn anyone.

Telling four identical bodies apart is not cosmetic. The comedy only lands if you know *whose* ragdoll
went off the cliff, which makes identity a mechanic, and it is why it is host-assigned rather than
client-chosen: two players cannot both be red.

`NetworkPlayerRegistry` is a **static, non-networked** index of the bodies FishNet has already
replicated onto this machine, keyed by owner id. The HUD draws a row per player, the Revive Machine
has to find a corpse's owner, natives pick a target, the scoreboard sums wallets — without it each of
those calls `FindObjectsByType` every frame, which is both slow and subtly wrong, because it also
finds bodies that are mid-despawn.

Bodies register themselves from `PlayerIdentity.OnStartNetwork` rather than being registered by the
spawner: that way clients populate their own registry from what they can already see, with no second
RPC, and a future dedicated server populates it too, where no client callback ever runs. On a client
the registry therefore holds everyone in observer range; on the host it holds everyone. Because the
state is static and a dropped connection does not despawn anything, `NetworkBootstrap` clears it once
both the server and the client have stopped.

**The player prefab is generated, not sculpted.** `PlayerPrefabBuilder` (editor, batchmode) writes
`Assets/_Project/Prefabs/Player.prefab` from a `Bone[]` table: 11 bones, 10 `CharacterJoint`s, a
`CharacterController`, and the whole combat stack. Every bone transform sits *unscaled at its joint
pivot* with the scaled primitive as a `Mesh` child, so resizing a body part never scales the bones
below it. Total mass is ~56 kg — light for a human, which is exactly why hits send people flying. The
builder also registers the prefab in `DefaultPrefabObjects`, because FishNet's auto-scan runs on asset
import and that does not reliably happen inside a single batchmode invocation.

---

---

## The signature mechanics

### What one hit does

Every weapon — fists included — embeds a `HitProfile`: damage, damage type, knockback, upward bias,
stun duration. Nothing about how a weapon feels is written in weapon code, so "a shotgun launches
you across the beach and a pistol does not" is a number in an asset that can be retuned without a
recompile.

| | damage | knockback | stun |
|---|---|---|---|
| Fists | low | ~4 | short |
| Bat | medium | ~10 | medium |
| Pistol | medium | ~0 | none |
| Shotgun | high | ~30 | long |
| Sniper | very high | ~45 | long |

Knockback and stun are separate numbers, but a hit meant to launch someone needs both: an upright
character is driven by its controller, not by physics, so the impulse only reads if the victim is
ragdolled. The one exception is a body already on the ground, which takes the impulse with no stun
duration at all — shooting a downed friend still sends them tumbling, which is most of the appeal.

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

Mechanically a ranged punch with a very long stun. What makes it worth carrying is the jitter: while
shocked, a random bone takes a small impulse ten times a second, and the victim's camera shakes.

**The jitter is not networked.** Ten impulses a second per victim is not worth the bandwidth, and it
does not need to be. The impulse step is derived from the network tick, and the random numbers come
from an integer hash of (object id, step), so every machine rolls identical values at the same moment
without a single extra packet. `ShockState` carries one SyncVar holding the whole shock — end tick,
jitter force, interval, shake — because those four values are one fact and separate SyncVars could
land out of order and briefly describe a shock nobody configured.

**Battery is the balance lever.** A taser with no ammunition cost would be strictly better than every
melee weapon, so the decision that matters is whether this target is worth a third of the charge. The
charge itself is also not streamed: a shot writes two values — the charge left at that moment, and the
tick recharging begins — and every peer recomputes the current level from the same formula the server
uses, so there is nothing to drift.

Both patterns are the same idea as the bleed-out timer: replicate the *rule* and the *deadline*, then
let every machine derive the continuously-changing number itself.

### Downed, abducted, dead

Running out of health does not kill you. `Health` has three states — `Alive`, `Downed`, `Dead` — and
the interesting one is the middle.

**Downed.** At 0 HP the character ragdolls, becomes carryable, and starts a bleed-out timer every
player can see. The deadline is stored as a FishNet network tick rather than a local timestamp, so
all four clients count down to the same moment and the HUD number is one everyone agrees with.
Getting picked up in time is cheap and free.

**Abducted.** Nothing stops a hostile native from picking up a downed body, because `Carryable` does
not care who is carrying — it only needs the carrier to have a `CarrySystem`. So natives haul downed
players back to their village and string them up while the timer keeps running. Rescue means
assaulting the village, which turns a teammate going down from a nuisance into an objective, and
gives the natives a reason to exist beyond wandering around. Killing them drops food, ammo and
materials, so the rescue pays for itself.

**Dead.** The timer expires and now it costs. The body has to be physically hauled to the Revive
Machine at base and paid for, at a price that scales with deaths this run. The whole loop is
deliberately inconvenient: the inconvenience is the content.

Dead players are not idle — they become ghosts that spectate and lightly push physics objects, so
they stay engaged and stay able to interfere.

The escalation is the point. Going down is recoverable, being carried off is a fight, and staying
dead is a bill.

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
