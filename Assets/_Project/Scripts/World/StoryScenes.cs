using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Audio;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The scenes themselves (#287), in story order. docs/STORY.md is the authority on what happens;
    /// this is how it is shot. Times in the comments are seconds into the scene. Gus, Kiki, Mo and
    /// Rex are the four friends (actors 0 to 3, the Body_Player_* models); Bogdan is actor 4.
    ///
    /// Positions in the attic are the set's own metres (tools/art/sets.py: the window is +x, the door
    /// -x, the table in the middle). On the island they are local to the scene: z away from the
    /// player towards the subject, x to the right, and <see cref="Frame"/> turns the lot until the
    /// cameras are clear.
    /// </summary>
    public partial class StoryBeat
    {
        /// <summary>Where the attic hangs: far over the island, where nothing else is.</summary>
        static readonly Vector3 Attic = new(0f, 900f, 0f);

        /// <summary>Where the Marisol sails in the storm: out at sea, past the edge of every island.</summary>
        static readonly Vector3 Storm = new(2600f, 0f, -2600f);

        const float Deck = 2.15f;

        static Vector3 A(float x, float y, float z) => Attic + new Vector3(x, y, z);

        static readonly Color Lantern = new(1f, 0.7f, 0.38f);
        static readonly Color Sun = new(1f, 0.93f, 0.78f);

        // ---------------------------------------------------------------------- 1 + 2: the prologue

        /// <summary>
        /// The attic, then the storm, under 40 s: Gus finds the tin box, the four gather round the
        /// journal and the chart, and three nights later the Marisol breaks on the reef.
        /// </summary>
        IEnumerator Prologue()
        {
            Transform set = BuildAttic(day: false);
            Grade(0.2f, new Color(1f, 0.92f, 0.8f), -5f, 8f);

            Puppet gus = Actor(0, A(-2.6f, 0f, 0.8f), 0f, set, false).Play("Fixing_Kneeling", 0f);
            Puppet kiki = Actor(1, A(1.3f, 0f, -1.5f), 180f, set, false).Play("Idle_Torch_Loop", 0f);
            Puppet mo = Actor(2, A(2.25f, 0f, -1.05f), 135f, set, false).Play("Push_Loop", 0f);
            Puppet rex = Actor(3, A(2.9f, 0f, 0.1f), 90f, set, false);
            Vector3 table = A(0.8f, 0f, 0.6f);

            // 0: the room, slowly, from the door.
            Caption("The attic of Radu Voinea, bush pilot. Missing since 1957.", 5.5f);
            Shot(A(-3.1f, 2.0f, 0.1f), A(1.5f, 1.0f, 0.3f), 52f, 6.5f, A(-2.2f, 1.8f, 0.2f), A(1.0f, 0.9f, 0.4f));
            yield return Fade(0f, 1.8f);
            yield return Wait(4f);

            // 6: Gus, at the trunk.
            Shot(A(-1.1f, 1.5f, 0.3f), A(-2.6f, 1.25f, 1.1f), 50f, 5f, A(-1.35f, 1.45f, 0.45f), A(-2.6f, 1.4f, 1.0f));
            yield return Wait(0.6f);
            gus.Play("PickUp_Table", 0.3f);
            yield return Wait(0.9f);
            Transform box = Prop("TinBox", A(-2.6f, 0.9f, 1.1f), Quaternion.identity, set);
            Transform lid = Prop("TinLid", new Vector3(0f, 0.12f, -0.13f), Quaternion.identity, box);
            gus.Hold(box);
            Say("GUS", "Guys. Guys! I found something.", 2.8f);
            yield return Wait(1.5f);

            gus.Walk(A(0.45f, 0f, 1.4f), face: table);
            kiki.Walk(A(-0.3f, 0f, 0.6f), face: table);
            yield return Wait(0.4f);
            mo.Walk(A(1.85f, 0f, 0.55f), face: table);
            rex.Walk(A(1.25f, 0f, 1.45f), face: table);
            yield return Wait(1.6f);

            // 11: round the table.
            Shot(A(0.8f, 1.6f, -0.8f), A(0.8f, 0.95f, 0.7f), 46f, 6f, A(0.8f, 1.45f, -0.5f), A(0.85f, 0.9f, 0.65f));
            for (float t = 0f; gus.Walking && t < 3f; t += Time.deltaTime) yield return null;
            gus.Play("Interact", 0.2f);
            yield return Wait(0.5f);
            gus.Drop();
            box.SetPositionAndRotation(A(0.95f, 0.8f, 0.6f), Quaternion.Euler(0f, 180f, 0f));
            gus.Play("Idle_Loop", 0.4f);
            yield return Turn(lid, Quaternion.identity, Quaternion.Euler(-105f, 0f, 0f), 0.9f);
            Say("KIKI", "Gus... that's your grandfather's journal.", 3f);
            yield return Wait(2.6f);

            // 17: the chart, from above, and the journal's last line.
            Prop("Journal", A(0.32f, 0.805f, 0.62f), Quaternion.Euler(0f, 75f, 0f), set);
            Shot(A(0.62f, 1.6f, 0.38f), A(0.62f, 0.8f, 0.62f), 38f, 5.5f, A(0.62f, 1.3f, 0.5f), A(0.62f, 0.8f, 0.64f));
            Card("Insulele de Cenușă", "The Ash Isles. On nobody else's chart.", 2.6f);
            yield return Wait(2.6f);
            Say("JOURNAL", "<i>\"The idol is real. The mountain is awake. I am going back for the others.\"</i>", 3.2f);
            yield return Wait(3.2f);

            // 23: and Mo, who has a question.
            Shot(A(2.9f, 1.7f, -1.6f), A(0.8f, 1.0f, 0.6f), 50f, 3f, A(2.7f, 1.65f, -1.4f));
            mo.Play("Idle_Talking_Loop", 0.3f);
            kiki.Face(mo.transform.position);
            rex.Face(mo.transform.position);
            gus.Face(mo.transform.position);
            Say("MO", "So... who's got a boat?", 2.2f);
            yield return Wait(2.2f);
            Sfx.Play2D(Sound.Thunder, 0.8f);
            yield return Fade(1f, 0.8f);

            // 26: three nights later.
            Destroy(set.gameObject);
            Actors.Clear();
            yield return null;
            yield return Wreck();
        }

        IEnumerator Wreck()
        {
            var boat = new GameObject("Marisol").transform;
            boat.SetParent(transform, false);
            boat.position = Storm;
            Prop("Marisol", Vector3.zero, Quaternion.identity, boat);
            Lamp(new Vector3(-0.55f, Deck + 2.35f, -1.6f), new Color(1f, 0.75f, 0.4f), 4f, 10f, boat);
            Lamp(new Vector3(0f, Deck + 1.4f, -0.9f), new Color(1f, 0.8f, 0.5f), 2.5f, 6f, boat);

            Puppet bogdan = Actor(4, new Vector3(0.35f, Deck, 0.8f), 0f, boat, false).Play("Idle_Talking_Loop", 0f);
            Actor(0, new Vector3(0.75f, Deck, 1.7f), 80f, boat, false).Play("Push_Loop", 0f);
            Actor(1, new Vector3(-0.7f, Deck, 1.3f), 300f, boat, false).Play("Idle_Torch_Loop", 0f);
            Actor(2, new Vector3(0.5f, Deck, -2.6f), 20f, boat, false).Play("Crouch_Idle_Loop", 0f);
            Actor(3, new Vector3(-0.75f, Deck, -3.0f), 270f, boat, false).Play("Push_Loop", 0f);

            Grade(-1.7f, new Color(0.62f, 0.72f, 0.95f), -45f, 18f);
            Ambience(Sound.Rain, 0.55f);
            Rain(true);
            _speed = 2.5f;
            StartCoroutine(Sail(boat));

            Caption("Three nights out. The Marisol, Captain Bogdan, no questions asked.", 3.5f);
            yield return Wait(1f);

            // 27: the boat in the weather, from the water.
            Shot(Storm + new Vector3(13f, 2.5f, 6f), Storm + new Vector3(0f, 2.5f, 2f), 45f, 4.5f, Storm + new Vector3(12f, 2.2f, 12f));
            Track(boat, new Vector3(0f, 2.5f, 0f));
            yield return Fade(0f, 1.2f);
            StartCoroutine(Lightning(0.7f));
            yield return Wait(3.3f);

            // 32: on deck.
            Shot(new Vector3(1.3f, 3.7f, 4.6f), new Vector3(0f, 3.0f, 0f), 50f, 4f, new Vector3(1.0f, 3.6f, 4.0f), ride: boat,
                 handheld: 1.4f);
            Shake(0.35f);
            Say("BOGDAN", "Hold on to something!", 2.2f);
            yield return Wait(2.2f);

            Transform reef = Prop("Reef", boat.position + new Vector3(1.5f, 0f, 17f), Quaternion.Euler(0f, 20f, 0f));
            reef.position = new Vector3(reef.position.x, WaterSurface.SeaLevel, reef.position.z);
            StartCoroutine(Lightning(0.4f));
            bogdan.Face(reef.position);
            Say("BOGDAN", "Rocks! ROCKS!", 1.6f);
            yield return Wait(1.4f);

            // 36: the reef, side on, and the Marisol going into it.
            Shot(boat.position + new Vector3(-15f, 2f, 10f), reef.position + Vector3.up * 1.5f, 42f, 2.5f, null,
                 boat.position + new Vector3(0f, 2f, 8f));
            Track(boat, new Vector3(0f, 2.5f, 3f));
            _speed = 6f;
            for (float t = 0f; reef.position.z - boat.position.z > 8f && t < 4f; t += Time.deltaTime) yield return null;

            Sfx.Play2D(Sound.Crash, 1f);
            Sfx.Play2D(Sound.Thunder, 1f);
            Shake(1f);
            White(1.3f);
            _speed = 0f;
            _jolt = 14f;
            yield return Wait(0.4f);
            Black(1f);
            White(0f);
            Rain(false);
            if (_ambience != null) _ambience.Stop();
            yield return Wait(1.2f);
        }

        float _speed, _jolt;

        /// <summary>The Marisol under way: riding the real sea's swell, pitching and rolling, a jolt on the rocks.</summary>
        IEnumerator Sail(Transform boat)
        {
            float z = 0f, speed = _speed;
            while (boat != null)
            {
                float t = Time.time;
                speed = Mathf.MoveTowards(speed, _speed, Time.deltaTime * 3f);
                z += speed * Time.deltaTime;
                _jolt = Mathf.MoveTowards(_jolt, 0f, Time.deltaTime * 8f);

                Vector3 at = Storm + new Vector3(0f, 0f, z);
                at.y = WaterSurface.HeightAt(at.x, at.z) - 1.05f + 0.35f * Mathf.Sin(t * 1.4f);
                boat.SetPositionAndRotation(at, Quaternion.Euler(6f * Mathf.Sin(t * 1.1f) + _jolt, 0f, 9f * Mathf.Sin(t * 0.8f + 1f)));
                yield return null;
            }
        }

        // ---------------------------------------------------------------------- 3: waking on the beach

        IEnumerator Wake()
        {
            Vector3 here = Here();
            Vector3[] lenses = { new(2.2f, 0.5f, -1.4f), new(0f, 1.6f, 5.5f), new(0.5f, 1.7f, -4f), new(0.5f, 4.5f, -6.5f) };
            Vector2[] spots = { new(0f, 0f), new(-1.6f, 0.8f), new(1.5f, -0.6f), new(-0.6f, -1.8f) };
            float yaw = Frame(here, Heading(here, _focus.position), lenses, spots);
            float[] lying = { 20f, -70f, 110f, 200f };

            var friends = new Puppet[4];
            for (int i = 0; i < 4; i++)
                friends[i] = Actor(i, Spot(here, yaw, spots[i].x, spots[i].y), yaw + lying[i]).Pose("Death01");

            Grade(0.1f, new Color(1f, 0.97f, 0.92f), 0f, 5f);

            // 0: eyes opening, twice.
            Shot(Lens(here, yaw, lenses[0]), friends[0].transform.position + Vector3.up * 0.3f, 40f, 5f,
                 Lens(here, yaw, new Vector3(2.0f, 0.7f, -1.1f)));
            yield return Fade(0.35f, 0.5f);
            yield return Fade(1f, 0.25f);
            yield return Fade(0.1f, 0.6f);
            yield return Fade(0.8f, 0.2f);
            yield return Fade(0f, 0.8f);
            yield return Wait(0.5f);

            for (int i = 0; i < 4; i++) StartCoroutine(GetUp(friends[i], i * 0.7f, _focus.position));
            yield return Wait(2.2f);

            // 5: the four of them, from the sea side.
            Shot(Lens(here, yaw, lenses[1]), here + Vector3.up * 0.9f, 45f, 5f, Lens(here, yaw, new Vector3(0f, 1.7f, 6.3f)));
            yield return Wait(1f);
            Say("MO", "Is everyone... alive?", 2.4f);
            yield return Wait(2.6f);
            Say("REX", "Define alive.", 1.8f);
            yield return Wait(2f);

            // 11: up and over them, to what brought them here.
            Shot(Lens(here, yaw, lenses[2]), here + Vector3.up * 1.2f, 50f, 6f, Lens(here, yaw, lenses[3]),
                 _focus.position + Vector3.up * 1.5f);
            Say("GUS", "Where's Bogdan?", 2.2f);
            yield return Wait(2.4f);
            Card(_title, _line, 4f);
            yield return Wait(4.2f);
        }

        IEnumerator GetUp(Puppet who, float delay, Vector3 look)
        {
            yield return Wait(delay);
            who.Play("Death01", 0.1f, 0.6f, reverse: true);
            for (float t = 0f; !who.Finished && t < 8f; t += Time.deltaTime) yield return null;
            who.Play("Idle_Loop", 0.4f);
            who.Face(look, 90f);
        }

        // ---------------------------------------------------------------------- 5: the far island

        IEnumerator Landing()
        {
            Vector3 here = Here();
            Vector3[] lenses = { new(1.0f, 0.5f, 2.2f), new(-0.9f, 1.5f, 1.6f), new(0.5f, 2.2f, -6f), new(0.5f, 4f, -8f) };
            Vector2[] ends = { new(-1.2f, -1f), new(-0.4f, -0.4f), new(0.5f, -0.6f), new(1.3f, -1.2f) };
            float yaw = Frame(here, Heading(here, _focus.position), lenses, ends);

            var friends = new Puppet[4];
            for (int i = 0; i < 4; i++)
            {
                friends[i] = Actor(i, Spot(here, yaw, ends[i].x * 1.2f, -7.5f - i * 0.4f), yaw);
                friends[i].Walk(Spot(here, yaw, ends[i].x, ends[i].y), face: _focus.position);
            }

            Grade(0.05f, Color.white, 5f, 5f);

            // 0: coming up out of the surf, from low on the sand.
            Shot(Lens(here, yaw, lenses[0]), here, 42f);
            Track(friends[1].transform, Vector3.up * 1.2f);
            yield return Fade(0f, 1f);
            yield return Wait(2.6f);

            // 3.6: Kiki, side on, as they come up the sand.
            Puppet kiki = friends[1];
            Shot(Lens(here, yaw, lenses[1]), kiki.Eyes, 36f);
            Track(kiki.Bone(HumanBodyBones.Head), Vector3.up * 0.05f);
            Say("KIKI", "Radu came this way.", 2.2f);
            yield return Wait(2.4f);
            Say("REX", "And look how that turned out.", 2.4f);
            yield return Wait(2.6f);

            // 8.6: the island, over their heads.
            Shot(Lens(here, yaw, lenses[2]), here + Vector3.up * 1.2f, 50f, 6f, Lens(here, yaw, lenses[3]),
                 _focus.position + Vector3.up * 1.5f);
            Card(_title, _line, 4.5f);
            yield return Wait(5f);
        }

        // ---------------------------------------------------------------------- 7: finding Bogdan

        IEnumerator Rescue()
        {
            Vector3 at = _focus.position;
            Vector3[] lenses =
            {
                new(3.5f, 1.5f, -5.5f), new(0.6f, 1.1f, -1.6f), new(0.7f, 1.75f, 0.9f), new(-0.9f, 1.45f, -1.7f), new(-4f, 2.5f, -1f),
            };
            Vector2[] spots = { new(-1.2f, -2.2f), new(-0.4f, -2.6f), new(0.5f, -2.5f), new(1.3f, -2.0f), new(0f, 0f) };
            float yaw = Frame(at, Heading(Here(), at), lenses, spots);

            Prop("Crate", Spot(at, yaw, 0f, 0.28f), Quaternion.Euler(0f, yaw, 0f));
            Puppet bogdan = Actor(4, Spot(at, yaw, 0f, 0f), yaw + 180f).Play("Sitting_Idle_Loop", 0f);
            bogdan.Ground = null;

            var friends = new Puppet[4];
            for (int i = 0; i < 4; i++)
            {
                friends[i] = Actor(i, Spot(at, yaw, spots[i].x * 1.4f, -8f - i * 0.5f), yaw);
                friends[i].Walk(Spot(at, yaw, spots[i].x, spots[i].y), face: at);
            }

            Grade(0.05f, Color.white, 0f, 6f);

            // 0: Bogdan, where they left him, and the four of them coming.
            Shot(Lens(at, yaw, lenses[0]), at + Vector3.up * 0.9f, 45f, 4.5f, Lens(at, yaw, new Vector3(3.0f, 1.4f, -4.6f)));
            yield return Fade(0f, 0.6f);
            yield return Wait(4f);

            // 4.5: he looks up.
            Shot(Lens(at, yaw, lenses[1]), bogdan.Eyes, 34f, 4f, Lens(at, yaw, new Vector3(0.5f, 1.2f, -1.4f)));
            Track(bogdan.Bone(HumanBodyBones.Head), Vector3.up * 0.05f);
            yield return Wait(0.5f);
            bogdan.Play("Sitting_Exit", 0.2f);
            yield return Wait(1.4f);
            Say("BOGDAN", "You came back.", 2.2f);
            for (float t = 0f; !bogdan.Finished && t < 3f; t += Time.deltaTime) yield return null;
            bogdan.Play("Idle_Talking_Loop", 0.4f);
            yield return Wait(0.6f);

            // 9: Gus, over Bogdan's shoulder.
            Puppet gus = friends[0];
            Shot(Lens(at, yaw, lenses[2]), gus.Eyes, 36f);
            Track(gus.Bone(HumanBodyBones.Head), Vector3.up * 0.05f);
            gus.Play("Idle_Talking_Loop", 0.3f);
            Say("GUS", "We said we would.", 2.2f);
            yield return Wait(2.4f);

            // 11.5: and back.
            gus.Play("Idle_Loop", 0.4f);
            Shot(Lens(at, yaw, lenses[3]), bogdan.Eyes, 36f);
            Track(bogdan.Bone(HumanBodyBones.Head), Vector3.up * 0.05f);
            Say("BOGDAN", "You said that in the storm, too.", 2.8f);
            yield return Wait(3f);
            Say("KIKI", "...Fair.", 1.4f);
            yield return Wait(1.6f);

            // 16: all of them, from the side, rising.
            Shot(Lens(at, yaw, lenses[4]), at + Vector3.up * 1.1f + Quaternion.Euler(0f, yaw, 0f) * Vector3.back,
                 48f, 6f, Lens(at, yaw, new Vector3(-5f, 4f, -2f)));
            Say("BOGDAN", "Then nobody gets left behind this time.", 2.8f);
            yield return Wait(2.8f);
            Card(_title, _line, 3.5f);
            yield return Wait(3.8f);
        }

        // ---------------------------------------------------------------------- the plane, whole

        IEnumerator Airworthy()
        {
            Transform plane = _focus;
            Bounds bounds = new(plane.position, Vector3.one);
            foreach (Renderer r in plane.GetComponentsInChildren<Renderer>())
                if (r.enabled && r is not ParticleSystemRenderer) bounds.Encapsulate(r.bounds);

            Vector3 at = plane.position;
            at.y = GroundAt(at);
            // The propeller says which way the nose is; the art model is not turned the way the greybox was.
            Transform propeller = plane.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.Contains("propeller"));
            Vector3 ahead = propeller != null ? Vector3.ProjectOnPlane(propeller.position - plane.position, Vector3.up) : plane.forward;
            float yaw = Heading(Vector3.zero, ahead);
            float nose = propeller != null ? ahead.magnitude + 0.3f : Vector3.Dot(bounds.max - plane.position, plane.forward) + 0.3f;
            float half = Mathf.Max(bounds.extents.x, bounds.extents.z);

            Puppet gus = Actor(0, Spot(at, yaw, 0.4f, nose + 0.7f), yaw + 180f).Play("Fixing_Kneeling", 0f);
            Actor(1, Spot(at, yaw, -1.4f, nose + 1.2f), yaw + 150f).Play("Idle_Torch_Loop", 0f);
            Puppet mo = Actor(2, Spot(at, yaw, 2.4f, nose - 0.8f), yaw - 90f).Play("Push_Loop", 0f);
            Puppet rex = Actor(3, Spot(at, yaw, -0.6f, nose + 2.6f), yaw + 180f).Play("Idle_Talking_Loop", 0f);
            AI.Castaway castaway = FindAnyObjectByType<AI.Castaway>();
            Puppet bogdan = castaway != null && Vector3.Distance(castaway.transform.position, at) < 40f
                ? Actor(4, Spot(at, yaw, 1.4f, nose + 3f), yaw + 200f)
                : null;

            Grade(0.05f, Color.white, 5f, 6f);

            // 0: three-quarters on, low, the whole aeroplane.
            Shot(Either(at, yaw, new Vector3(half * 0.9f, 1.0f, nose + half * 0.7f)), bounds.center, 44f, 5f,
                 Either(at, yaw, new Vector3(half * 0.8f, 1.3f, nose + half * 0.5f)));
            yield return Fade(0f, 0.6f);
            yield return Wait(1f);
            Say("GUS", "Tighten that... and that... and...", 2.8f);
            yield return Wait(3.2f);

            // 4.8: Gus at the engine.
            Shot(Either(at, yaw, new Vector3(1.6f, 0.9f, nose + 2.1f)), gus.Eyes, 36f, 3.5f);
            Track(gus.Bone(HumanBodyBones.Head), Vector3.down * 0.2f);
            yield return Wait(1f);
            gus.Play("Idle_Loop", 0.5f);
            Say("GUS", "...done?", 1.6f);
            yield return Wait(2f);

            // 7.8: all of them, pleased with themselves.
            Shot(Either(at, yaw, new Vector3(-half * 1.1f, 2.2f, nose + half * 0.6f)), bounds.center + Vector3.up * 0.3f, 50f, 7f,
                 Either(at, yaw, new Vector3(-half * 1.3f, 3.2f, nose + half * 0.9f)));
            foreach (Puppet actor in Actors) actor.Play("Dance_Loop", 0.4f, Random.Range(0.9f, 1.1f));
            Say("REX", "It's held together with rope.", 2.4f);
            yield return Wait(2.6f);
            Say(bogdan != null ? "BOGDAN" : "KIKI", "Most things are.", 2f);
            yield return Wait(2.2f);
            Card(_title, _line, 3.2f);
            yield return Wait(3.4f);
        }

        /// <summary>A lens position, or its mirror across the scene if the first is in the scenery.</summary>
        Vector3 Either(Vector3 origin, float yaw, Vector3 local)
        {
            Vector3 at = Lens(origin, yaw, local);
            if (!Physics.CheckSphere(at, 0.35f, ~0, QueryTriggerInteraction.Ignore) && Clear(at, origin + Vector3.up)) return at;
            return Lens(origin, yaw, new Vector3(-local.x, local.y, local.z));
        }

        // ---------------------------------------------------------------------- 10: the epilogue

        IEnumerator Epilogue()
        {
            // The aeroplane going, if it is still in sight: a lens out ahead of it, watching it pass.
            PlaneAssembly plane = FindAnyObjectByType<PlaneAssembly>();
            Camera main = Camera.main;
            if (plane != null && main != null && Vector3.Distance(plane.transform.position, main.transform.position) < 400f)
            {
                Vector3 p = plane.transform.position;
                Vector3 ahead = Vector3.ProjectOnPlane(plane.transform.forward, Vector3.up).normalized;
                Vector3 lens = p + ahead * 70f + Vector3.Cross(Vector3.up, ahead) * 22f + Vector3.down * 4f;
                lens.y = Mathf.Max(lens.y, WaterSurface.SeaLevel + 2f);
                Black(0f);
                Shot(lens, p, 38f, 5f, null, null, 30f);
                Track(plane.transform, Vector3.zero);
                yield return Wait(3.5f);
                yield return Fade(1f, 1.5f);
            }

            Black(1f);
            Shot(A(-3.0f, 1.8f, 0.9f), A(1.2f, 1.0f, 0.4f), 50f);
            Caption("Two weeks later.", 3f);
            yield return Wait(2.2f);

            Transform set = BuildAttic(day: true);
            Grade(0.15f, new Color(1f, 0.95f, 0.86f), 5f, 5f);
            Puppet gus = Actor(0, A(1.75f, 0f, 1.3f), 180f, set, false).Play("Sitting_Idle_Loop", 0f);
            Puppet kiki = Actor(1, A(-0.25f, 0f, 0.55f), 90f, set, false).Play("Idle_Talking_Loop", 0f);
            Puppet mo = Actor(2, A(0.9f, 0f, -0.45f), 20f, set, false);
            Puppet rex = Actor(3, A(2.6f, 0f, -0.4f), 300f, set, false);
            Puppet bogdan = Actor(4, A(3.0f, 0f, 0.35f), 250f, set, false);
            Prop("TinBox", A(0.35f, 0.8f, 0.45f), Quaternion.Euler(0f, 20f, 0f), set);
            Prop("Journal", A(0.65f, 0.805f, 0.85f), Quaternion.Euler(0f, -15f, 0f), set);
            Prop("Photo", A(1.1f, 0.8f, 0.95f), Quaternion.identity, set);

            // 2: the attic again, in daylight.
            Shot(A(-3.0f, 1.8f, 0.9f), A(1.2f, 1.0f, 0.4f), 50f, 6f, A(-2.4f, 1.7f, 0.8f), A(1.3f, 1.0f, 0.5f));
            yield return Fade(0f, 1.5f);
            Say("BOGDAN", "So. The plane.", 2.2f);
            yield return Wait(2.6f);
            Say("KIKI", "Sank. Mostly.", 1.8f);
            yield return Wait(2.2f);

            // 8.5: the photograph, over Gus's shoulder.
            Shot(A(1.6f, 1.3f, 1.8f), A(1.1f, 0.92f, 0.95f), 34f, 4f, A(1.45f, 1.15f, 1.55f));
            gus.Play("Sitting_Talking_Loop", 0.4f);
            Say("GUS", "He'd have liked you, Bogdan.", 2.8f);
            yield return Wait(3.4f);

            // 12: Bogdan, at the window.
            Shot(A(1.5f, 1.6f, -0.7f), bogdan.Eyes, 36f, 3.5f);
            Track(bogdan.Bone(HumanBodyBones.Head), Vector3.up * 0.05f);
            bogdan.Face(A(1.0f, 0f, 0.7f));
            bogdan.Play("Idle_Talking_Loop", 0.4f);
            foreach (Puppet friend in new[] { kiki, mo, rex }) friend.Face(bogdan.transform.position);
            Say("BOGDAN", "He did. Once.", 2.4f);
            yield return Wait(3.2f);

            // 15.5: the room, and Mo, who has another question.
            Shot(A(-2.6f, 1.75f, 0.6f), A(1.3f, 1.0f, 0.3f), 52f, 4f, A(-2.2f, 1.95f, 0.4f));
            mo.Play("Idle_Talking_Loop", 0.3f);
            Say("MO", "...Same time next year?", 2.2f);
            yield return Wait(2.6f);
            yield return Fade(1f, 1.6f);
        }

        // ---------------------------------------------------------------------- after the credits

        IEnumerator Radio()
        {
            Transform set = BuildAttic(day: false, lit: false);
            Grade(-0.4f, new Color(0.6f, 0.7f, 1f), -30f, 10f);
            Beam(A(6f, 4f, 0.2f), A(1.5f, 0f, -1.2f), new Color(0.6f, 0.72f, 1f), 18f, 12f, 40f).transform.SetParent(set, true);
            Prop("Radio", A(2.2f, 0.86f, -2.34f), Quaternion.identity, set);
            Prop("TinBox", A(1.55f, 0.86f, -2.34f), Quaternion.Euler(0f, 8f, 0f), set);
            Light dial = Lamp(A(2.33f, 1.09f, -2.1f), new Color(1f, 0.6f, 0.25f), 0f, 2.5f, set);

            Black(1f);
            Shot(A(1.7f, 1.35f, -0.4f), A(2.2f, 1.0f, -2.3f), 45f, 13f, A(2.05f, 1.12f, -1.35f), null, 30f);
            yield return Fade(0f, 2f);

            for (int i = 0; i < 3; i++)
            {
                Sfx.Play2D(Sound.Radio, 0.8f);
                StartCoroutine(Glow(dial, 3.6f));
                Say("RADIO", i switch
                {
                    0 => "...Marisol... Marisol, this is Yankee-Romeo, Victor-Romeo-Victor...",
                    1 => "...the Ash Isles... nineteen fifty-seven... does anyone read me?",
                    _ => "...I am still here.",
                }, 3.4f);
                yield return Wait(3.8f);
            }

            yield return Fade(1f, 1.5f);
        }

        IEnumerator Glow(Light light, float seconds)
        {
            for (float t = 0f; t < seconds && light != null; t += Time.deltaTime)
            {
                light.intensity = 1.6f * Mathf.Clamp01(Mathf.Min(t, seconds - t) * 3f) * (0.8f + 0.2f * Mathf.PerlinNoise(t * 9f, 0f));
                yield return null;
            }

            if (light != null) light.intensity = 0f;
        }

        // ---------------------------------------------------------------------- 4: Radu, 1957 (#275)

        /// <summary>
        /// The first journal page, picked up at the back of the cave, and the man who left it there:
        /// Radu, kneeling where the finder stands, writing the page, tearing it out and setting it on
        /// the rock, then walking out into the light. In sepia, staged round the finder.
        /// </summary>
        IEnumerator Flashback()
        {
            Vector3 here = _focus != null ? _focus.position : Here();
            Vector3[] lenses = { new(2.4f, 1.3f, 2.4f), new(1.1f, 1.1f, 1.4f), new(2.8f, 1.5f, 0.2f), new(0.4f, 1.6f, 3f) };
            Vector2[] spots = { new(0f, 0f), new(0f, -5f) };
            float yaw = Frame(here, Heading(Here(), here), lenses, spots);
            Vector3 radu = Spot(here, yaw, 0f, 0f);
            Vector3 page = Spot(here, yaw, 0f, 0.5f) + Vector3.up * 0.03f;
            Vector3 door = Spot(here, yaw, 0f, -6f);

            // The back of a cave: a lens that lands in the rock comes in towards Radu until it is out,
            // but never closer than a metre and a half.
            Vector3 L(Vector3 local)
            {
                Vector3 at = Lens(here, yaw, local), to = radu + Vector3.up * 1.1f;
                for (int k = 0; k < 6 && Vector3.Distance(at, to) > 1.5f
                                && Physics.CheckSphere(at, 0.3f, ~0, QueryTriggerInteraction.Ignore); k++)
                    at = Vector3.Lerp(at, to, 0.15f);
                return at;
            }

            Puppet pilot = Actor(5, radu, yaw).Play("Crouch_Idle_Loop", 0f);
            Prop("Journal", page, Quaternion.Euler(0f, yaw + 90f, 0f));
            Grade(0.55f, new Color(1f, 0.84f, 0.6f), -75f, 12f);
            // A key light on him, and the day in through the mouth of the cave behind.
            Lamp(Spot(here, yaw, 1.2f, 1.6f) + Vector3.up * 2f, Sun, 4f, 7f);
            Lamp(Spot(here, yaw, 0.4f, -3f) + Vector3.up * 2.2f, Sun, 3f, 10f);
            White(1f);
            Black(0f);

            // 0: Radu crouched over the journal, the camera easing in.
            Caption("Wreck Island, 1957.", 4f);
            Shot(L(lenses[0]), radu + Vector3.up * 0.7f, 40f, 7f, L(new Vector3(2f, 1.2f, 2f)));
            yield return Wait(1.2f);
            Say("RADU", "Set her down on the beach at first light. There are lights on the hill at night.", 4.2f);
            yield return Wait(4.4f);

            // 5.6: the page, close.
            Shot(L(lenses[1]), page + Vector3.up * 0.35f, 40f, 4f, L(new Vector3(0.8f, 0.95f, 1f)));
            Say("RADU", "The reef opens only at low water, along the line I drew. Nowhere else.", 3.8f);
            yield return Wait(2f);
            pilot.Play("Interact", 0.25f);
            Sfx.Play2D(Sound.Pickup, 0.5f);
            yield return Wait(2f);

            // 9.6: he leaves it there, and gets up.
            Shot(L(lenses[2]), radu + Vector3.up * 1f, 42f, 4.5f, L(new Vector3(2.5f, 1.6f, 0f)), radu + Vector3.up * 1.4f);
            Say("RADU", "I'll leave the chart here, where the rain can't reach it.", 3.2f);
            pilot.Play("Crouch_Idle_Loop", 0.3f);
            yield return Wait(1.6f);
            pilot.Play("Idle_Loop", 0.6f);
            yield return Wait(2f);

            // 13.6: and out, towards the light, from behind.
            pilot.Walk(door);
            Shot(L(lenses[3]), radu + Vector3.up * 1.2f, 48f, 5f, null, door + Vector3.up * 1.4f);
            Say("RADU", "Follow the line, not the lights. - R.V.", 3.4f);
            yield return Wait(3.6f);
            Card(_title, _line, 3.5f);
            yield return Wait(2.5f);
            yield return Fade(1f, 1f);
        }

        // ---------------------------------------------------------------------- anything else

        /// <summary>A beat with no scene of its own: a slow push on the subject and its title.</summary>
        IEnumerator Glimpse()
        {
            Vector3 at = _focus != null ? _focus.position : Here();
            float yaw = Heading(Here(), at);
            Shot(Lens(at, yaw, new Vector3(4f, 2f, -7f)), at + Vector3.up * 1.2f, 45f, 6f, Lens(at, yaw, new Vector3(3f, 1.8f, -5.5f)));
            yield return Fade(0f, 0.5f);
            Card(_title, _line, 4.5f);
            yield return Wait(5.5f);
        }

        // ---------------------------------------------------------------------- the attic

        /// <summary>The attic set with its light: the lantern and a fill, and the sun through the window by day.</summary>
        Transform BuildAttic(bool day, bool lit = true)
        {
            var set = new GameObject("Attic").transform;
            set.SetParent(transform, false);
            Prop("Attic", Attic, Quaternion.identity, set);
            if (!lit) return set;

            Light lantern = Lamp(A(0.8f, 2.2f, 0.25f), Lantern, day ? 1.2f : 3.5f, 7f, set);
            Lamp(A(-1.8f, 1.6f, 0.6f), new Color(1f, 0.85f, 0.65f), 1.2f, 6f, set);
            Beam(A(5.5f, 3.6f, 0.4f), A(1f, 0f, 0.3f), Sun, day ? 60f : 25f, 12f, 38f).transform.SetParent(set, true);
            StartCoroutine(Flicker(lantern));
            return set;
        }

        static IEnumerator Flicker(Light light)
        {
            float base_ = light.intensity;
            while (light != null)
            {
                light.intensity = base_ * (0.9f + 0.1f * Mathf.PerlinNoise(Time.time * 6f, 0.5f));
                yield return null;
            }
        }

        static IEnumerator Turn(Transform what, Quaternion from, Quaternion to, float seconds)
        {
            for (float t = 0f; t < seconds && what != null; t += Time.deltaTime)
            {
                what.localRotation = Quaternion.Slerp(from, to, Mathf.SmoothStep(0f, 1f, t / seconds));
                yield return null;
            }

            if (what != null) what.localRotation = to;
        }
    }
}
