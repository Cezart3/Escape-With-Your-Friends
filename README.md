# Escape With Your Friends

A goofy 4-player co-op survival game. You and your friends are stranded on an island.
Survive, progress, make money, buy a boat, reach a second and far more dangerous island,
repair a plane, rescue an NPC, and get out.

The point is not realism. The point is that the physics are bad on purpose and your friends
are a hazard. You can punch them, stun them, taser them, pick them up, throw them off cliffs,
run them over, and revive them with a very stupid machine.

**Tone references:** *How to Fish*, *Gamble with your Friends*, *Content Warning*.
**Structure reference:** *Sons of the Forest*, but much smaller and much dumber.

---

## Stack

| Layer | Choice | Why |
|---|---|---|
| Engine | **Unity 6 LTS (6000.x)**, URP | Text-based assets (YAML + C#) are editable and diffable from a terminal; URP runs on integrated graphics; free under $200k revenue |
| Language | **C#** | Fast iteration, no 20-minute builds |
| Networking | **FishNet** (free) | Good prediction/reconciliation, free, proven in this genre |
| Transport | **Facepunch.Steamworks** + FishNet Steam transport | Steam Datagram Relay — no port forwarding, no servers, no monthly cost |
| Topology | **Host-authoritative P2P** | One player hosts. Same model as Lethal Company |
| 3D content | **Blender 5.1** via blender-mcp | Low-poly assets generated and iterated from the terminal |

**Unreal Engine 5 was evaluated and rejected.** Blueprints and `.uasset` files are binary, which makes
terminal-driven and agent-assisted development largely blind, and the UE5 baseline is too heavy for the
"runs on almost any PC" requirement.

## Target

- 4 players (tested up to 8)
- 60fps at 1080p on a **Radeon 760M iGPU** — that is the min-spec bench, not an afterthought
- Island 1 is roughly 1 km² (1024×1024 units); island 2 is smaller, denser, and meaner

---

## Authority model

Everything that matters is decided by the host. Clients are never trusted.

| System | Authority |
|---|---|
| Own movement, driven vehicle | Client owner, predicted + server-reconciled |
| Damage, HP, death | Host |
| Economy, inventory, shop | Host |
| RNG — roulette, loot, fishing | **Host, always** |
| AI (natives, animals) | Host |
| Ragdoll, carry | Host simulates, clients interpolate |

Tick rate 20–30Hz is enough. Network jank on ragdolls is part of the charm, not a bug to chase.

---

## Repository layout

```
Assets/_Project/
  Scripts/
    Core/       GameManager, ServiceLocator, SaveSystem
    Net/        FishNet bootstrap, SteamLobby, NetworkPlayerRegistry
    Player/     Movement, Look, Interaction, Inventory, Stats
    Combat/     Damage, StunSystem, CarrySystem, Ragdoll, Weapons
    Vehicles/   CarController, BoatBuoyancy, PlaneController
    AI/         NativeAI, AnimalAI, ShopNPC, CasinoNPC
    World/      TerrainGenerator, POISpawner, DayNightCycle
    Economy/    Wallet, ShopSystem, UpgradeSystem
    Casino/     Roulette, ChipSystem, AlcoholBuff
    UI/         HUD, InventoryUI, ShopUI, CasinoUI
    Editor/     Terrain and data generation tools
  Data/         ScriptableObjects: WeaponDef, ItemDef, UpgradeDef, BuffDef, FishDef
  Prefabs/  Scenes/  Art/  Audio/
docs/           DESIGN.md, ARCHITECTURE.md
```

**Core principle: everything is data-driven through ScriptableObjects.**
A new weapon, item, upgrade, or fish is a `.asset` text file that can be authored from the terminal.
Content scales without touching code or the editor.

---

## Milestones

Tracked as GitHub milestones with issues. See the [issue tracker](../../issues).

| # | Milestone | Est. | Deliverable |
|---|---|---|---|
| M0 | Setup | 1w | Engine, repo, MCP tooling, project skeleton |
| M1 | Networked Core | 4w | Steam lobby, 4 players, punch/stun/ragdoll/carry/taser/death/revive in a greybox arena |
| M2 | Island 1 | 5w | Procedural terrain, water, day/night, POI greybox, NavMesh |
| M3 | Survival + Inventory | 4w | Stats, networked inventory, gathering, crafting, storage |
| M4 | Economy + Combat | 5w | Shop NPC, weapons + upgrades, hunting, fishing, hostile natives |
| M5 | Vehicles | 5w | Car, boat with buoyancy, upgrades, network sync |
| M6 | Casino | 3w | Roulette, chips, alcohol NPC with buffs and blurred vision |
| M7 | Island 2 + Ending | 6w | Second island, plane parts, flight, NPC rescue, ending |
| M8 | Art + Polish | 8w | Character art, animation, audio, UI, min-spec perf pass |
| M9 | Steam Ship | 6w | Steamworks, store page, demo, ratings, release |

**M1 is a gate.** Build it, hand it to three friends, and play the greybox arena.
If you do not laugh for 20 minutes with no other content in the game, the core is wrong and
no amount of island content will fix it. See the GATE issue.

---

## Budget

| Item | Cost |
|---|---|
| Unity Personal, FishNet, Blender, Mixamo, Poly Haven (CC0), Freesound | 0 |
| **Steam Direct fee** | **$100 USD** — non-refundable, recoupable after $1,000 adjusted gross revenue |
| **Total minimum** | **~$100** |

Steam requires a tax interview (W-8BEN for an individual) and can take up to 30 days to verify,
plus a mandatory 30-day wait between store page approval and release. Both happen in M9.

---

## Getting started

```powershell
# Unity Hub — run from an ELEVATED terminal, the installer needs admin
winget install --id Unity.UnityHub -e --force

# Blender (already installed at C:\Program Files\Blender Foundation\Blender 5.1)
winget install --id BlenderFoundation.Blender -e
```

Then from Unity Hub: sign in, install **Unity 6 LTS (6000.x)** with the
*Windows Build Support (IL2CPP)* module, and open this folder as a project.

### Build headless

```
Unity.exe -quit -batchmode -nographics -projectPath . \
  -executeMethod EscapeWithYourFriends.EditorTools.BuildTool.PerformBuild
```

Optional flags: `-buildOutput <dir>`, `-development`, `-scriptingBackend <il2cpp|mono>`.
Defaults to IL2CPP into `BuildOutput/`. The build fails with exit code 1 if no scenes are
enabled in Build Settings, rather than shipping a player that opens to nothing.

**IL2CPP needs a C++ toolchain.** Unity detects it through `vswhere.exe` plus the registry key
`HKLM\SOFTWARE\Wow6432Node\Microsoft\Microsoft SDKs\Windows\v10.0`, so a bare compiler on PATH is
not enough — it has to be a registered Visual Studio installation:

```powershell
winget install --id Microsoft.VisualStudio.2022.BuildTools -e --override `
  "--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

`-scriptingBackend mono` builds without it, which is the faster loop for iterating. Use IL2CPP for
anything you ship.

### One-time project configuration

These are idempotent and already applied; re-run them after a clean clone.

```
-executeMethod EscapeWithYourFriends.EditorTools.PackageBootstrap.Install       # packages
-executeMethod EscapeWithYourFriends.EditorTools.ProjectSetup.Run               # URP, layers, tags
-executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.CreateBootstrapScene
```

---

## Agent-assisted development

This project is built with [Claude Code](https://claude.com/claude-code) driving the terminal.

- **MCP for Unity** ([CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp)) — editor control, scene and asset operations, console reading, test runs. **Requires the Unity Editor to be open.**
- **blender-mcp** ([ahujasid/blender-mcp](https://github.com/ahujasid/blender-mcp)) — low-poly modelling, plus CC0 assets from Poly Haven.

What the agent cannot do: playtesting, tuning game feel, and final art direction. Those stay human.
