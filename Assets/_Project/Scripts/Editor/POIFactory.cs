using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.World;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Turns <see cref="POICatalog"/> into things standing on the island.
    ///
    /// Two jobs, and the order between them is the whole design. The catalog's pads are read by
    /// <see cref="IslandShape"/>, so the catalog has to be attached to the profile *before* a single
    /// height is sampled - flattening the baked heightmap afterwards would leave the splatmap and
    /// fourteen thousand trees believing in the hillside that used to be there. Then, once the
    /// terrain exists, the placements are resolved and baked into the scene's spawner.
    ///
    /// Adding a point of interest is an append to POIs.asset and one regeneration. Nothing is
    /// dragged into a scene, and the diff is seven readable lines.
    /// </summary>
    public static class POIFactory
    {
        /// <summary>Profile id of the second island; the first one is anything else. See #68.</summary>
        internal const string SecondIslandId = "Island2";

        /// <summary>
        /// Where an island keeps its catalog. The first island keeps the name it has always had, so
        /// its asset, its GUID and every scene reference to it survive this becoming a function.
        /// </summary>
        public static string CatalogPathFor(IslandProfile profile)
            => profile != null && profile.Id == SecondIslandId
                ? "Assets/_Project/Data/POIs2.asset"
                : "Assets/_Project/Data/POIs.asset";

        const string ReviveMachinePrefabPath = "Assets/_Project/Prefabs/ReviveMachine.prefab";
        const string HangPointPrefabPath = "Assets/_Project/Prefabs/HangPoint.prefab";

        /// <summary>
        /// The catalog, created from the defaults below the first time. Created once and then left
        /// alone, because the entire point is that a human edits it; -rebuildPois starts over.
        /// </summary>
        public static POICatalog EnsureCatalog(IslandProfile profile)
        {
            bool rebuild = CommandLine.HasFlag("-rebuildPois");
            string path = CatalogPathFor(profile);
            var catalog = AssetDatabase.LoadAssetAtPath<POICatalog>(path);

            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<POICatalog>();
                catalog.Entries = DefaultEntries(profile);

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AssetDatabase.CreateAsset(catalog, path);
                Debug.Log($"[POIFactory] Generated {path} with {catalog.Entries.Length} entries.");
            }
            else if (rebuild)
            {
                catalog.Entries = DefaultEntries(profile);
                EditorUtility.SetDirty(catalog);
                Debug.Log($"[POIFactory] Rebuilt {path} from code, hand edits discarded (-rebuildPois).");
            }

            // Attaching it to the profile is what puts the pads into the height function. Done here
            // rather than by hand so a clean clone generates the same island as a working copy.
            if (profile.Pois != catalog)
            {
                profile.Pois = catalog;
                EditorUtility.SetDirty(profile);
                Debug.Log("[POIFactory] Attached the catalog to the island profile; its pads are now part of the shape.");
            }

            return catalog;
        }

        /// <summary>
        /// Resolves every entry against the island and writes the result into the spawner. Entries
        /// whose prefab does not exist yet are reported and skipped rather than failing the build:
        /// the catalog is allowed to describe the shop and the casino before #36 has built them.
        /// </summary>
        public static int Bake(IslandProfile profile, POISpawner spawner)
        {
            POICatalog catalog = profile.Pois;
            if (catalog == null || spawner == null) return 0;

            var shape = new IslandShape(profile);
            var so = new SerializedObject(spawner);
            SerializedProperty placements = so.FindProperty("_placements");

            var resolved = new List<(POIEntry entry, NetworkObject prefab, Vector3 position)>();
            var seen = new HashSet<string>();
            int missing = 0;

            foreach (POIEntry entry in catalog.Entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Id)) continue;

                if (!seen.Add(entry.Id))
                    Debug.LogError($"[POIFactory] Two entries share the id '{entry.Id}'; lookups by id will be a coin flip.");

                float ground = shape.HeightAt(entry.Position.x, entry.Position.y);
                float y = entry.SnapToGround ? ground + entry.YOffset : entry.YOffset;
                var position = new Vector3(entry.Position.x, y, entry.Position.y);

                Validate(entry, shape, ground);

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.PrefabPath);
                NetworkObject networkObject = prefab != null ? prefab.GetComponent<NetworkObject>() : null;

                if (prefab == null)
                {
                    Debug.LogWarning($"[POIFactory] '{entry.Id}' wants {entry.PrefabPath}, which does not exist yet. "
                                     + "Placed nothing; the pad under it is still in the terrain.");
                    missing++;
                    continue;
                }

                if (networkObject == null)
                {
                    Debug.LogError($"[POIFactory] '{entry.Id}' points at {entry.PrefabPath}, which has no "
                                   + "NetworkObject. It cannot be spawned.");
                    missing++;
                    continue;
                }

                resolved.Add((entry, networkObject, position));
            }

            // Rewritten whole rather than appended to: this array is generated output, and a second
            // run must not leave two revive machines standing in the same spot.
            placements.arraySize = resolved.Count;
            for (int i = 0; i < resolved.Count; i++)
            {
                (POIEntry entry, NetworkObject prefab, Vector3 position) = resolved[i];

                SerializedProperty element = placements.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("Id").stringValue = entry.Id;
                element.FindPropertyRelative("Prefab").objectReferenceValue = prefab;
                element.FindPropertyRelative("Position").vector3Value = position;
                element.FindPropertyRelative("Euler").vector3Value = new Vector3(0f, entry.Yaw, 0f);

                Debug.Log($"[POIFactory] {entry.Id} -> {entry.PrefabPath} at "
                          + $"({position.x:F1}, {position.y:F1}, {position.z:F1}), yaw {entry.Yaw:F0}"
                          + (entry.PadRadius > 0f ? $", pad {entry.PadRadius}m" : "") + ".");
            }

            so.FindProperty("_catalog").objectReferenceValue = catalog;
            so.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log($"[POIFactory] Baked {resolved.Count} of {catalog.Entries.Length} catalog entries into the "
                      + $"island spawner" + (missing > 0 ? $"; {missing} have no prefab yet" : "") + ".");

            return resolved.Count;
        }

        /// <summary>
        /// The checks worth making at bake time. None of them refuse to place anything - a POI in a
        /// silly spot is a tuning problem, not a build failure - but all of them are invisible until
        /// somebody walks over there, which is exactly the kind of thing a log line is for.
        /// </summary>
        static void Validate(POIEntry entry, IslandShape shape, float ground)
        {
            if (!entry.AllowUnderwater && ground <= IslandShape.SeaLevel)
            {
                Debug.LogError($"[POIFactory] '{entry.Id}' is at {ground:F1}m, under the sea, and is not "
                               + "marked AllowUnderwater.");
            }

            float slope = shape.SlopeAt(entry.Position.x, entry.Position.y);
            if (slope > entry.MaxSlope)
            {
                Debug.LogWarning($"[POIFactory] '{entry.Id}' stands on a slope of {slope:F2}, over its limit of "
                                 + $"{entry.MaxSlope:F2}. Give it a pad or move it.");
            }

            float half = shape.Profile.Size * 0.5f;
            if (Mathf.Abs(entry.Position.x) > half || Mathf.Abs(entry.Position.y) > half)
                Debug.LogError($"[POIFactory] '{entry.Id}' is outside the island square.");
        }


        // ---------------------------------------------------------------- the defaults

        const string GreyboxDir = "Assets/_Project/Prefabs/World";

        /// <summary>What a landmark wants from the ground it stands on.</summary>
        struct SiteWish
        {
            public float WantedHeight;
            public float MinHeight;
            public float MaxHeight;
            public Vector2 Reference;
            public float MinFromReference;
            public float MaxFromReference;
            public float FlatWeight;
            public float Separation;
            public float FootprintRadius;
        }

        /// <summary>
        /// The catalog as it ships. Nothing here is a typed-in coordinate: every landmark is searched
        /// for against the island it is going to stand on, so a different seed puts the village
        /// inland on *that* island rather than in that island's sea.
        ///
        /// After the first generation they are numbers in a text file like everything else, and a
        /// human is free to drag any of them somewhere better.
        /// </summary>
        static POIEntry[] DefaultEntries(IslandProfile profile)
        {
            // Searched against the island with no pads in it, which is the state the catalog is being
            // written for: the pads these searches choose are the ones that will exist afterwards.
            var bare = ScriptableObject.CreateInstance<IslandProfile>();
            EditorUtility.CopySerialized(profile, bare);
            bare.Pois = null;

            if (profile.Id == SecondIslandId) return SecondIslandEntries(bare);

            var shape = new IslandShape(bare);
            var taken = new List<Vector2>();

            // Camp first, because everything else is placed relative to it: the shop and the casino
            // are a walk, the village is a hike, and the cave is somewhere in between.
            Vector2 camp = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 4.5f, MinHeight = 1.5f, MaxHeight = 12f,
                MaxFromReference = 0f, FlatWeight = 0.6f, FootprintRadius = 12f
            }, "camp");

            Vector2 shop = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 8f, MinHeight = 2f, MaxHeight = 22f, Reference = camp,
                MinFromReference = 70f, MaxFromReference = 160f,
                FlatWeight = 0.7f, Separation = 40f, FootprintRadius = 8f
            }, "shop");

            Vector2 casino = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 6f, MinHeight = 2f, MaxHeight = 20f, Reference = camp,
                MinFromReference = 90f, MaxFromReference = 210f,
                FlatWeight = 0.7f, Separation = 60f, FootprintRadius = 10f
            }, "casino");

            float casinoFacing = Facing(casino, camp);

            // Far enough that walking into it is a decision rather than an accident.
            Vector2 village = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 20f, MinHeight = 6f, MaxHeight = 48f, Reference = camp,
                MinFromReference = 240f, MaxFromReference = 600f,
                FlatWeight = 0.9f, Separation = 90f, FootprintRadius = 18f
            }, "village");

            // On the tideline. It is the first thing seen from the water and it is where the boat
            // parts come from, so it belongs half in the sea.
            Vector2 wreck = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 0.3f, MinHeight = -1.5f, MaxHeight = 2f, Reference = camp,
                MinFromReference = 60f, MaxFromReference = 320f,
                FlatWeight = 0.4f, Separation = 50f, FootprintRadius = 10f
            }, "wreck");

            Vector2 cave = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 34f, MinHeight = 18f, MaxHeight = 80f, Reference = camp,
                MinFromReference = 120f, MaxFromReference = 500f,
                FlatWeight = 0.5f, Separation = 70f, FootprintRadius = 10f
            }, "cave");

            // Moored off the camp beach. It is placed on the seabed a metre or so under and floats
            // itself up within the first second, which needs no new placement plumbing at all:
            // buoyancy is already the thing that decides where a boat's waterline is.
            Vector2 mooring = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = -1.2f, MinHeight = -3f, MaxHeight = -0.3f, Reference = camp,
                MinFromReference = 40f, MaxFromReference = 160f,
                FlatWeight = 0.7f, Separation = 40f, FootprintRadius = 12f
            }, "mooring");

            // #73. The first island gets a strip of its own, for the same reason each island keeps
            // its own hull at its own mooring: an airframe is a scene object, scene objects do not
            // cross scenes, and flying back to fetch somebody would otherwise be a one-way trip.
            // Further from camp than the second island's, because this island is twice the size and
            // there is more flat ground to be choosy about.
            Vector2 strip = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 6f, MinHeight = 2f, MaxHeight = 20f, Reference = camp,
                MinFromReference = 60f, MaxFromReference = 190f,
                FlatWeight = 0.95f, Separation = 40f, FootprintRadius = 30f
            }, "plane");

            Object.DestroyImmediate(bare);

            float campFacing = Facing(camp, Vector2.zero);
            float shopFacing = Facing(shop, camp);
            float villageFacing = Facing(village, camp);

            return new[]
            {
                Entry("camp.base", GreyboxDir + "/BaseCamp.prefab", camp, campFacing,
                      pad: 22f, falloff: 16f, raise: 0.5f, maxSlope: 0.28f),

                // The machine stands inside the camp's own pad rather than on one of its own: two
                // overlapping pads at different heights make a step in the middle of the camp.
                Entry("camp.revive", ReviveMachinePrefabPath, camp + Offset(campFacing, 9f),
                      campFacing + 180f, pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.3f),

                // #43's starting bench. On the camp's pad for the same reason the machine is, and off
                // to one side of it so the two do not fight over the same square metre. One bench is
                // given rather than crafted because the first recipe a player needs is the one that
                // makes a bench possible somewhere else.
                Entry("camp.bench", StationBuilder.BenchPath,
                      camp + Offset(campFacing + 70f, 8f), campFacing + 250f,
                      pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.3f),

                // #44's chests. Two rather than one, and next to each other, because "the food chest
                // and the parts chest" is a thing four players will agree on in about a minute and
                // one chest gives them nothing to agree about.
                // Eight metres out and behind the camp, in the gap between two of the four spawn
                // points on the 6.5m ring. Anything closer than that ring is somewhere a player
                // materialises, and spawning inside a chest is a bug report rather than a joke.
                Entry("camp.chest.a", StorageBuilder.ChestPath,
                      camp + Offset(campFacing + 140f, 8f), campFacing + 320f,
                      pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.3f),

                Entry("camp.chest.b", StorageBuilder.ChestPath,
                      camp + Offset(campFacing + 155f, 8f), campFacing + 335f,
                      pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.3f),

                // #57's buggy. Parked at the camp rather than found somewhere, because the thing
                // M5 has to prove first is that four people can get into one vehicle - and a vehicle
                // you have to walk half the island to reach is a vehicle three of them never see.
                // Behind the camp and nose-out, which is the only arc nothing else claims: the
                // revive machine is dead ahead, the bench at +70, the chests at +140 and +155, and
                // the shelter fills the quarter around -90. It was parked at -75 and spent every
                // physics step of the first harness run wedged against Shelter.Post2 with ten
                // kilonewtons of contact, spinning all four wheels and going nowhere - a car cannot
                // push a static collider, and every prop in this camp is static.
                //
                // It gets a pad of its own, unlike everything else on the camp's. The camp's pad is
                // 22m wide with 16m of falloff, so its flat core is only about 6m - and 6m is inside
                // the spawn ring. Anything parked outside that ring is standing on the ramp, which
                // for a 3.6m car with 0.5m of belly clearance means one end is in a hillside: the
                // second harness run had it pushing 7kN of terrain sideways at chassis height and
                // going nowhere. The raise matches the camp's so the two pads do not make a step.
                Entry("camp.buggy", VehicleBuilder.BuggyPath,
                      camp + Offset(campFacing + 195f, 11f), campFacing + 195f,
                      pad: 10f, falloff: 6f, raise: 0.5f, maxSlope: 0.3f),

                Entry("shop", GreyboxDir + "/Shop.prefab", shop, shopFacing,
                      pad: 12f, falloff: 12f, raise: 0.4f, maxSlope: 0.3f),

                // #48's trader, on the camp side of the shop so you walk up to the counter rather
                // than round the building looking for it. On the shop's own pad, like the revive
                // machine is on the camp's.
                Entry("shop.counter", ShopFactory.CounterPath, shop + Offset(shopFacing, 5f),
                      shopFacing + 180f, pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.3f),

                Entry("casino", GreyboxDir + "/Casino.prefab", casino, casinoFacing,
                      pad: 14f, falloff: 12f, raise: 0.4f, maxSlope: 0.3f),

                // #63's cage. Two windows side by side on the way in, three metres apart so the
                // crosshair picks one without ambiguity: chips on the left, cash on the right. They
                // are separate objects rather than one booth with two verbs because the server
                // resolves an interaction to the first IInteractable on a NetworkObject, and
                // aiming is a thing players already know how to do.
                Entry("casino.chips", CasinoFactory.BuyWindowPath,
                      casino + Offset(casinoFacing, 7f) + Offset(casinoFacing + 90f, -1.6f),
                      casinoFacing + 180f, pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.3f),

                Entry("casino.cash", CasinoFactory.CashWindowPath,
                      casino + Offset(casinoFacing, 7f) + Offset(casinoFacing + 90f, 1.6f),
                      casinoFacing + 180f, pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.3f),

                // The table, inside the shell. #65 builds the room around it.
                Entry("casino.table", CasinoFactory.TablePath, casino, casinoFacing + 180f,
                      pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.3f),

                // #66's barman, in the gap the greybox leaves between the bar and the back wall, at
                // the room's own BarNpcStand. Placed exactly rather than on the metre grid every
                // other entry uses: that gap is 40cm, and half a metre of rounding puts him inside
                // a wall.
                Entry("casino.bar", CasinoFactory.BarmanPath,
                      casino + Offset(casinoFacing, -3.62f) + Offset(casinoFacing + 90f, -2.6f),
                      casinoFacing, pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.3f, exact: true),

                Entry("village", GreyboxDir + "/NativeVillage.prefab", village, villageFacing,
                      pad: 24f, falloff: 20f, raise: 0.3f, maxSlope: 0.32f),

                // #108's prison, on the village's own pad at the PrisonSite the greybox already
                // marked. Six metres behind the totem, which is the *far* side from base camp -
                // because the village faces the way you come from, getting to the hooks means going
                // through the huts rather than round them, and a rescue that could be done from the
                // treeline would not be a raid.
                //
                // Three of them, in a row, three metres apart. Three because a four-player game can
                // lose three people and still have somebody left to come and get them, and because
                // a village with one hook would silently drop the second body on the ground; the
                // fourth is deliberately missing, since a wipe is a wipe and does not need scenery.
                Entry("village.prison.a", HangPointPrefabPath,
                      village + Offset(villageFacing + 180f, 6f) + Offset(villageFacing + 90f, -3f),
                      villageFacing, pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.35f),

                Entry("village.prison.b", HangPointPrefabPath,
                      village + Offset(villageFacing + 180f, 6f),
                      villageFacing, pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.35f),

                Entry("village.prison.c", HangPointPrefabPath,
                      village + Offset(villageFacing + 180f, 6f) + Offset(villageFacing + 90f, 3f),
                      villageFacing, pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.35f),

                Entry("wreck", GreyboxDir + "/Wreck.prefab", wreck, Facing(wreck, camp),
                      pad: 10f, falloff: 14f, raise: 0f, maxSlope: 0.5f, allowUnderwater: true),

                Entry("cave", GreyboxDir + "/Cave.prefab", cave, Facing(cave, camp),
                      pad: 13f, falloff: 16f, raise: 0.2f, maxSlope: 0.45f),

                // Nose out to sea, so the first thing a driver does is leave rather than reverse off
                // the beach.
                Entry("boat", BoatBuilder.BoatPath, mooring, Facing(mooring, camp) + 180f,
                      pad: 12f, falloff: 10f, raise: 0f, maxSlope: 0.3f, allowUnderwater: true),

                // #73. The group's other aeroplane. It stands here whole, because PlaneAssembly
                // remembers that this group has already built one - see the note on its Owned flag.
                Entry("plane", PlaneBuilder.PlanePath, strip, Facing(strip, camp),
                      pad: 30f, falloff: 20f, raise: 0.2f, maxSlope: 0.25f),

                // And the person. At the wreck, on the camp side: somebody stranded at a shipwreck
                // is a sentence you can read from the air, and putting them anywhere with a roof
                // would raise the question of why they never walked to the camp.
                //
                // Sixteen metres out, not six. Six was inside the hull, which the bake said out
                // loud - "setting off 13m from the marker because the marker is inside the
                // building" - and a navmesh agent standing in a sealed pocket of mesh has
                // isOnNavMesh true and nowhere to walk. The wreck's own pad is 10m, so this is the
                // first open sand past it.
                Entry("castaway", CastawayBuilder.CastawayPath,
                      wreck + Offset(Facing(wreck, camp), 16f), Facing(wreck, camp) + 180f,
                      pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.5f)
            };
        }

        /// <summary>
        /// The second island's catalog: five entries against the first island's twenty.
        ///
        /// That is the design and not a shortcut. The first island is where the game is played -
        /// a shop, a casino, a bench, a buggy, somewhere to sleep - and the second is somewhere you
        /// go to take something and leave. What stands on it is a beachhead, the machine that undoes
        /// a death, and three places that want you dead. Everything that makes a landing worthwhile
        /// belongs to the issues that come after this one: the boat that gets you there (#69) and the
        /// plane parts that are the reason to go (#70).
        ///
        /// The ids are deliberately the ones the first island uses. <c>NativeFactory</c>,
        /// <c>AnimalFactory</c> and the spawn-point writer all look landmarks up by name, and giving
        /// this island its own names would mean teaching each of them which island it is on.
        /// </summary>
        static POIEntry[] SecondIslandEntries(IslandProfile bare)
        {
            var shape = new IslandShape(bare);
            var taken = new List<Vector2>();

            // The beachhead, and the only flat thing on the island. Low and near the water because
            // this is where the boat ties up, and everything else is placed as a walk from here.
            Vector2 camp = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 3.5f, MinHeight = 1f, MaxHeight = 9f,
                MaxFromReference = 0f, FlatWeight = 0.75f, FootprintRadius = 11f
            }, "camp");

            // Half the island means half the distances. A hundred metres here is what two hundred is
            // over there: far enough to be a decision, close enough that it finds you first.
            Vector2 village = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 24f, MinHeight = 6f, MaxHeight = 60f, Reference = camp,
                MinFromReference = 110f, MaxFromReference = 220f,
                FlatWeight = 0.85f, Separation = 70f, FootprintRadius = 18f
            }, "village");

            // Uphill, but on a shelf. The first pass asked for 55m with a low flatness weight and
            // got a mouth halfway up a cliff: the pad flattened the ground it stands on and nothing
            // could walk to it, which NavFactory caught and the grid-based reachability check did
            // not. Height is worth less than being connected to the rest of the island.
            Vector2 cave = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 30f, MinHeight = 12f, MaxHeight = 60f, Reference = camp,
                MinFromReference = 90f, MaxFromReference = 200f,
                FlatWeight = 0.85f, Separation = 60f, FootprintRadius = 12f
            }, "cave");

            Vector2 wreck = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 0.3f, MinHeight = -1.5f, MaxHeight = 2f, Reference = camp,
                MinFromReference = 60f, MaxFromReference = 200f,
                FlatWeight = 0.4f, Separation = 50f, FootprintRadius = 10f
            }, "wreck");

            // #69. The far island gets its own hull at its own mooring, because a boat is a scene
            // object and scene objects do not cross scenes. That is also the whole of "losing the
            // boat is recoverable": wherever you land, something is tied up waiting.
            Vector2 mooring = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = -1.2f, MinHeight = -3f, MaxHeight = -0.3f, Reference = camp,
                MinFromReference = 25f, MaxFromReference = 70f,
                FlatWeight = 0.7f, Separation = 30f, FootprintRadius = 12f
            }, "mooring");

            // #71. The airframe, and eventually the strip it leaves from. It wants the flattest
            // ground on an island that has almost none, which is why FlatWeight is nearly all of the
            // score and the height it asks for is a suggestion. Near the beachhead because every
            // part in the game has to be carried here on foot, at a third of walking pace.
            Vector2 strip = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 5f, MinHeight = 1.5f, MaxHeight = 16f, Reference = camp,
                MinFromReference = 35f, MaxFromReference = 95f,
                FlatWeight = 0.95f, Separation = 28f, FootprintRadius = 30f
            }, "plane");

            float campFacing = Facing(camp, village);

            return new[]
            {
                Entry("camp.base", GreyboxDir + "/BaseCamp.prefab", camp, campFacing,
                      pad: 12f, falloff: 16f, raise: 0.2f, maxSlope: 0.3f),

                // A death on the far island with no way back up is a run ended by a boat ride, so
                // the machine comes ashore with you. It costs what it costs on the first island.
                Entry("camp.revive", ReviveMachinePrefabPath, camp + Offset(campFacing, 9f),
                      campFacing + 180f, pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.3f),

                Entry("village", GreyboxDir + "/NativeVillage.prefab", village, Facing(village, camp),
                      pad: 24f, falloff: 20f, raise: 0.3f, maxSlope: 0.32f),

                Entry("cave", GreyboxDir + "/Cave.prefab", cave, Facing(cave, camp),
                      pad: 13f, falloff: 16f, raise: 0.2f, maxSlope: 0.45f),

                Entry("wreck", GreyboxDir + "/Wreck.prefab", wreck, Facing(wreck, camp),
                      pad: 10f, falloff: 14f, raise: 0f, maxSlope: 0.5f, allowUnderwater: true),

                Entry("boat", BoatBuilder.BoatPath, mooring, Facing(mooring, camp) + 180f,
                      pad: 12f, falloff: 10f, raise: 0f, maxSlope: 0.3f, allowUnderwater: true),

                // #70. One plane part at each of the three places that want you dead, which is the
                // whole reason those three places are on this island. They flatten nothing and raise
                // nothing: a part is a crate on the ground, and a crate that came with its own patch
                // of level terrain would be a crate somebody had put there for you.
                //
                // Inside the danger rather than beside it. The propeller sits ten metres into the
                // village, which is closer to the huts than anything else on either island gets.
                Entry("part.propeller", PlanePartBuilder.PropellerPath,
                      village + Offset(Facing(village, camp), 10f), Facing(village, camp),
                      pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.6f),

                Entry("part.wing", PlanePartBuilder.WingPath,
                      cave + Offset(Facing(cave, camp), 5f), Facing(cave, camp) + 90f,
                      pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.6f),

                Entry("part.engine", PlanePartBuilder.EnginePath,
                      wreck + Offset(Facing(wreck, camp), 6f), Facing(wreck, camp),
                      pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.6f),

                // #71. This one does get a pad, and the biggest on the island: a plane standing on a
                // slope is a plane that will not be taking off in #72.
                //
                // #72 widened it. Full throttle is 9000N on 1100kg, so the take-off roll is about
                // twenty-five metres and a 22m pad ran out from under it half way. A strip is the one
                // pad on the island that has a length requirement rather than a footprint.
                Entry("plane", PlaneBuilder.PlanePath, strip, Facing(strip, camp),
                      pad: 30f, falloff: 20f, raise: 0.2f, maxSlope: 0.25f)
            };
        }

        static POIEntry Entry(string id, string prefab, Vector2 position, float yaw, float pad,
                              float falloff, float raise, float maxSlope, bool allowUnderwater = false,
                              bool exact = false)
            => new POIEntry
            {
                Id = id,
                PrefabPath = prefab,

                // Whole metres, because a baked catalogue somebody reads is nicer than one full of
                // 137.4182. `exact` is for the few placements where the slack is smaller than the
                // rounding - see casino.bar.
                Position = exact ? position
                                 : new Vector2(Mathf.Round(position.x), Mathf.Round(position.y)),
                Yaw = Mathf.Round(yaw),
                SnapToGround = true,
                PadRadius = pad,
                PadFalloff = falloff,
                PadRaise = raise,
                MaxSlope = maxSlope,
                AllowUnderwater = allowUnderwater
            };

        /// <summary>Degrees that turn <paramref name="from"/> to look at <paramref name="at"/>.</summary>
        static float Facing(Vector2 from, Vector2 at)
        {
            Vector2 delta = at - from;
            return delta.sqrMagnitude < 0.001f ? 0f : Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg;
        }

        static Vector2 Offset(float yaw, float distance)
        {
            float radians = yaw * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians)) * distance;
        }

        /// <summary>
        /// The best ground on the island for one landmark, by search rather than by eye. Scored on how
        /// close the height is to what the place wants and how flat the ground is across its whole
        /// footprint, because a metre of noise at the sample point says nothing about the twenty
        /// metres the building will actually sit on.
        /// </summary>
        static Vector2 Site(IslandShape shape, IslandProfile profile, List<Vector2> taken,
                            SiteWish wish, string label)
        {
            const int steps = 110;

            float half = profile.Size * 0.5f;
            float step = profile.Size / steps;

            Vector2 best = Vector2.zero;
            float bestScore = float.MinValue;

            for (int j = 1; j < steps; j++)
            {
                float z = -half + j * step;
                for (int i = 1; i < steps; i++)
                {
                    float x = -half + i * step;
                    var candidate = new Vector2(x, z);

                    float height = shape.HeightAt(x, z);
                    if (height < wish.MinHeight || height > wish.MaxHeight) continue;

                    if (wish.MaxFromReference > 0f)
                    {
                        float distance = Vector2.Distance(candidate, wish.Reference);
                        if (distance < wish.MinFromReference || distance > wish.MaxFromReference) continue;
                    }

                    bool crowded = false;
                    foreach (Vector2 other in taken)
                    {
                        if (Vector2.Distance(candidate, other) >= wish.Separation) continue;
                        crowded = true;
                        break;
                    }

                    if (crowded) continue;

                    float footprint = Mathf.Max(4f, wish.FootprintRadius);
                    float roughness = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        float angle = k * Mathf.PI * 0.5f;
                        roughness += Mathf.Abs(shape.HeightAt(x + Mathf.Cos(angle) * footprint,
                                                              z + Mathf.Sin(angle) * footprint) - height);
                    }

                    float score = -Mathf.Abs(height - wish.WantedHeight) - roughness * wish.FlatWeight;

                    // A nudge inland, so nothing ends up tucked into a corner of the square with half
                    // the island a long walk away.
                    score -= candidate.magnitude / profile.Size;

                    if (score <= bestScore) continue;

                    bestScore = score;
                    best = new Vector2(Mathf.Round(x), Mathf.Round(z));
                }
            }

            taken.Add(best);
            Debug.Log($"[POIFactory] {label} site ({best.x}, {best.y}), ground "
                      + $"{shape.HeightAt(best.x, best.y):F1}m, score {bestScore:F2}.");
            return best;
        }

        // ---------------------------------------------------------------- reachability

        /// <summary>
        /// Whether you can walk from the camp to each of the others, and how far it is.
        ///
        /// The acceptance criterion for #36 is that all six landmarks are reachable on foot, and that
        /// is a claim about the terrain rather than about the prefabs, so it is checked against the
        /// shape: a flood fill with a cost over an eight-metre grid of cells that are above water and
        /// no steeper than a character controller can climb. It is not a NavMesh - that is #37 - but
        /// a NavMesh cannot invent a route the terrain does not have.
        /// </summary>
        public static void ReportReachability(IslandProfile profile)
        {
            POICatalog catalog = profile.Pois;
            if (catalog == null || catalog.Entries.Length == 0) return;

            const float cell = 8f;
            const float maxSlope = 0.8f;      // about 39 degrees, past which a character controller stalls
            const float minHeight = 0.25f;

            var shape = new IslandShape(profile);
            int side = Mathf.Max(8, Mathf.RoundToInt(profile.Size / cell));
            float half = profile.Size * 0.5f;

            var walkable = new bool[side, side];
            int walkableCells = 0;

            for (int j = 0; j < side; j++)
            {
                float z = -half + (j + 0.5f) * cell;
                for (int i = 0; i < side; i++)
                {
                    float x = -half + (i + 0.5f) * cell;
                    walkable[j, i] = shape.HeightAt(x, z) > minHeight && shape.SlopeAt(x, z, 4f) <= maxSlope;
                    if (walkable[j, i]) walkableCells++;
                }
            }

            POIEntry start = catalog.Find("camp.base") ?? catalog.Entries[0];
            if (!Cell(start.Position, half, cell, side, out int startX, out int startZ))
            {
                Debug.LogError("[POIFactory] The camp is off the reachability grid; nothing was checked.");
                return;
            }

            var distance = new float[side, side];
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                    distance[j, i] = float.MaxValue;

            // Breadth-first with a cost, which on a uniform grid with only two edge lengths comes out
            // close enough to Dijkstra to report a walking distance anyone would recognise.
            var queue = new Queue<Vector2Int>();
            distance[startZ, startX] = 0f;
            queue.Enqueue(new Vector2Int(startX, startZ));

            int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] dz = { 0, 0, 1, -1, 1, -1, 1, -1 };

            while (queue.Count > 0)
            {
                Vector2Int at = queue.Dequeue();
                for (int k = 0; k < 8; k++)
                {
                    int nx = at.x + dx[k];
                    int nz = at.y + dz[k];
                    if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                    if (!walkable[nz, nx]) continue;

                    float cost = distance[at.y, at.x] + (k < 4 ? cell : cell * 1.41421f);
                    if (cost >= distance[nz, nx]) continue;

                    distance[nz, nx] = cost;
                    queue.Enqueue(new Vector2Int(nx, nz));
                }
            }

            int reached = 0;
            int total = 0;

            foreach (POIEntry entry in catalog.Entries)
            {
                if (entry == null) continue;
                total++;

                if (!Cell(entry.Position, half, cell, side, out int ex, out int ez))
                {
                    Debug.LogError($"[POIFactory] {entry.Id} is off the reachability grid entirely.");
                    continue;
                }

                // A landmark on the tideline sits on a cell that is under water by a few centimetres,
                // so the search widens until it finds walkable ground. It starts at the centre cell
                // and grows one ring at a time rather than taking the best of a wide neighbourhood:
                // a fixed three-cell window quietly shaves up to thirty metres off every distance it
                // reports, which turns a measurement into a flattering guess.
                float best = float.MaxValue;
                int slack = 0;

                for (; slack <= 3 && best >= float.MaxValue; slack++)
                    best = Nearest(distance, ex, ez, side, slack);

                if (best >= float.MaxValue)
                {
                    Debug.LogError($"[POIFactory] {entry.Id} cannot be walked to from the camp: "
                                   + "water or a cliff is in the way.");
                    continue;
                }

                reached++;
                int rings = slack - 1;
                Debug.Log($"[POIFactory] {entry.Id} is {best:F0}m of walking from the camp"
                          + (rings > 0 ? $", landing {rings * cell:F0}m short of its centre - the "
                                       + "ground under it is not walkable" : "") + ".");
            }

            Debug.Log($"[POIFactory] {reached} of {total} landmarks reachable on foot across "
                      + $"{walkableCells} walkable cells of {cell}m at up to {maxSlope:F1} gradient.");
        }

        static bool Cell(Vector2 position, float half, float cell, int side, out int x, out int z)
        {
            x = Mathf.FloorToInt((position.x + half) / cell);
            z = Mathf.FloorToInt((position.y + half) / cell);
            return x >= 0 && z >= 0 && x < side && z < side;
        }

        /// <summary>Shortest distance in any cell within <paramref name="reach"/> cells of this one.</summary>
        static float Nearest(float[,] distance, int x, int z, int side, int reach)
        {
            float best = float.MaxValue;

            for (int j = -reach; j <= reach; j++)
            {
                for (int i = -reach; i <= reach; i++)
                {
                    int nx = x + i;
                    int nz = z + j;
                    if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                    best = Mathf.Min(best, distance[nz, nx]);
                }
            }

            return best;
        }
    }
}
