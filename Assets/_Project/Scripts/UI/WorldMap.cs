using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.World;
using UnityEngine;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// The island on M. The user got lost: the only guide was the objective banner's distance.
    ///
    /// The picture is baked once from the terrain, on first open: sea, sand, grass and rock by
    /// height, with a hillshade off the terrain's normals so hills read as hills. Over it, every
    /// <see cref="Landmark"/> by name (hostile ones in red), the squad as coloured dots, you as an
    /// arrow, and the objective as a gold ring.
    ///
    /// IMGUI like the hurt edge and the key prompt: one texture and a few labels, drawn only while
    /// open. Added by <see cref="HudRoot"/>, so a headless peer has none; <see cref="Bake"/> is static
    /// so -lookTest can still check the picture.
    /// </summary>
    public class WorldMap : MonoBehaviour
    {
        const int Resolution = 512;

        /// <summary>Open on screen. The windowed playthrough sets it for its screenshot.</summary>
        internal static bool Open;

        Texture2D _picture;
        Terrain _bakedFrom;
        Rect _view;
        readonly System.Collections.Generic.List<Rect> _taken = new();
        GUIStyle _label, _hint;

        void Update()
        {
            var keys = UnityEngine.InputSystem.Keyboard.current;
            if (keys == null) return;
            if (keys.mKey.wasPressedThisFrame) Open = !Open;
            else if (Open && keys.escapeKey.wasPressedThisFrame) Open = false;
        }

        /// <summary>
        /// The terrain as a top-down picture, one pixel per sample, north up. Null without a terrain
        /// (the cave, the menu).
        /// </summary>
        internal static Texture2D Bake(Terrain terrain, int resolution)
        {
            if (terrain == null || terrain.terrainData == null) return null;

            TerrainData data = terrain.terrainData;
            float baseY = terrain.GetPosition().y;
            var pixels = new Color32[resolution * resolution];
            Vector3 sun = new Vector3(-0.5f, 0.75f, 0.45f).normalized;

            for (int y = 0; y < resolution; y++)
            for (int x = 0; x < resolution; x++)
            {
                float u = (x + 0.5f) / resolution, v = (y + 0.5f) / resolution;
                float height = baseY + data.GetInterpolatedHeight(u, v) - IslandShape.SeaLevel;
                Vector3 normal = data.GetInterpolatedNormal(u, v);

                Color colour;
                if (height < 0f)
                    colour = Color.Lerp(new Color(0.36f, 0.72f, 0.78f), new Color(0.07f, 0.22f, 0.4f),
                                        Mathf.Clamp01(-height / 6f));
                else
                {
                    colour = height < 1.2f ? new Color(0.9f, 0.84f, 0.62f)
                           : height < 18f ? Color.Lerp(new Color(0.42f, 0.66f, 0.3f), new Color(0.24f, 0.45f, 0.2f), height / 18f)
                           : Color.Lerp(new Color(0.5f, 0.47f, 0.42f), new Color(0.82f, 0.8f, 0.76f), Mathf.Clamp01((height - 18f) / 25f));
                    // Steep is rock wherever it is, and the light falls from the north-west.
                    if (normal.y < 0.75f) colour = Color.Lerp(colour, new Color(0.48f, 0.44f, 0.38f), 0.6f);
                    colour *= 0.65f + 0.45f * Mathf.Clamp01(Vector3.Dot(normal, sun));
                }

                colour.a = 1f;
                pixels[y * resolution + x] = colour;
            }

            var texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false)
            {
                name = "WorldMap", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        /// <summary>
        /// The part of the terrain worth showing: the land plus a band of sea, square, in terrain
        /// 0..1. The whole terrain is mostly sea, and at that scale the camp's four buildings were
        /// one blur of labels.
        /// </summary>
        internal static Rect LandView(Terrain terrain)
        {
            TerrainData data = terrain.terrainData;
            float baseY = terrain.GetPosition().y;
            float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f;
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                float u = (x + 0.5f) / 64f, v = (y + 0.5f) / 64f;
                if (baseY + data.GetInterpolatedHeight(u, v) < IslandShape.SeaLevel) continue;
                minX = Mathf.Min(minX, u); maxX = Mathf.Max(maxX, u);
                minY = Mathf.Min(minY, v); maxY = Mathf.Max(maxY, v);
            }
            if (maxX <= minX) return new Rect(0f, 0f, 1f, 1f);

            float side = Mathf.Min(1f, Mathf.Max(maxX - minX, maxY - minY) * 1.15f);
            float cx = Mathf.Clamp((minX + maxX) * 0.5f, side * 0.5f, 1f - side * 0.5f);
            float cy = Mathf.Clamp((minY + maxY) * 0.5f, side * 0.5f, 1f - side * 0.5f);
            return new Rect(cx - side * 0.5f, cy - side * 0.5f, side, side);
        }

        /// <summary>Where a world position lands on the terrain, 0..1 each way, north up.</summary>
        internal static Vector2 ToMap(Terrain terrain, Vector3 world)
        {
            Vector3 origin = terrain.GetPosition(), size = terrain.terrainData.size;
            return new Vector2((world.x - origin.x) / size.x, (world.z - origin.z) / size.z);
        }

        void OnGUI()
        {
            if (!Open) return;

            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null) return;
            if (_bakedFrom != terrain)
            {
                if (_picture != null) Destroy(_picture);
                _picture = Bake(terrain, Resolution);
                _view = LandView(terrain);
                _bakedFrom = terrain;
            }

            float scale = Screen.height / 1080f;
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Overflow };
                _hint = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            }
            _label.fontSize = Mathf.RoundToInt(17f * scale);
            _hint.fontSize = Mathf.RoundToInt(18f * scale);

            Color was = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            float side = Screen.height * 0.86f;
            var frame = new Rect((Screen.width - side) * 0.5f, Screen.height * 0.04f, side, side);
            GUI.DrawTextureWithTexCoords(frame, _picture, _view);

            Vector2 Place(Vector3 world)
            {
                Vector2 m = ToMap(terrain, world);
                m = new Vector2((m.x - _view.x) / _view.width, (m.y - _view.y) / _view.height);
                return new Vector2(frame.x + m.x * frame.width, frame.yMax - m.y * frame.height);
            }

            // Dots first, then each name in the first of above, below, right or left that hits no
            // other name or dot. The camp's buildings sit thirty metres apart.
            _taken.Clear();
            foreach (Landmark landmark in Landmark.All)
            {
                if (landmark == null) continue;
                Vector2 at = Place(landmark.transform.position);
                Dot(at, 9f * scale, landmark.Hostile ? new Color(0.9f, 0.15f, 0.12f) : new Color(1f, 0.95f, 0.8f));
                _taken.Add(new Rect(at.x - 6f * scale, at.y - 6f * scale, 12f * scale, 12f * scale));
            }
            foreach (Landmark landmark in Landmark.All)
            {
                if (landmark == null) continue;
                Vector2 at = Place(landmark.transform.position);
                Vector2 size = _label.CalcSize(new GUIContent(landmark.DisplayName));
                float gap = 8f * scale;
                Rect best = default;
                foreach (Vector2 corner in new[]
                         {
                             new Vector2(at.x - size.x * 0.5f, at.y - gap - size.y),
                             new Vector2(at.x - size.x * 0.5f, at.y + gap),
                             new Vector2(at.x + gap, at.y - size.y * 0.5f),
                             new Vector2(at.x - gap - size.x, at.y - size.y * 0.5f),
                         })
                {
                    var rect = new Rect(corner, size);
                    if (best == default) best = rect;
                    if (_taken.TrueForAll(t => !t.Overlaps(rect))) { best = rect; break; }
                }
                _taken.Add(best);
                Shadowed(best, landmark.DisplayName, landmark.Hostile ? new Color(1f, 0.45f, 0.4f) : Color.white);
            }

            if (Objective.Target != null)
            {
                Vector2 at = Place(Objective.Target.position);
                Ring(at, 15f * scale, new Color(1f, 0.8f, 0.15f));
            }

            foreach (NetworkPlayerRegistry.PlayerBody body in NetworkPlayerRegistry.Players)
            {
                if (!body.IsValid) continue;
                Vector2 at = Place(body.Object.transform.position);

                if (!body.Object.IsOwner)
                {
                    Dot(at, 11f * scale, body.Identity.Color);
                    continue;
                }

                // You, as an arrow along the camera, which is where you are looking, not where the
                // capsule happens to face.
                Camera eye = Camera.main;
                float yaw = eye != null ? eye.transform.eulerAngles.y : body.Object.transform.eulerAngles.y;
                Matrix4x4 matrix = GUI.matrix;
                GUIUtility.RotateAroundPivot(yaw, at);
                GUI.color = Color.black;
                GUI.Label(new Rect(at.x - 20f * scale + 1.5f, at.y - 20f * scale + 1.5f, 40f * scale, 40f * scale), "▲", Arrow(scale));
                GUI.color = body.Identity.Color;
                GUI.Label(new Rect(at.x - 20f * scale, at.y - 20f * scale, 40f * scale, 40f * scale), "▲", Arrow(scale));
                GUI.matrix = matrix;
            }

            Shadowed(new Rect(frame.x, frame.yMax + 4f * scale, frame.width, 30f * scale),
                     "M or Esc  close        ▲ you    ● squad    ○ objective    red = dangerous", Color.white, _hint);
            GUI.color = was;
        }

        GUIStyle _arrow;
        GUIStyle Arrow(float scale)
        {
            _arrow ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            _arrow.fontSize = Mathf.RoundToInt(30f * scale);
            return _arrow;
        }

        static void Dot(Vector2 at, float size, Color colour)
        {
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(at.x - size * 0.5f - 2f, at.y - size * 0.5f - 2f, size + 4f, size + 4f), Texture2D.whiteTexture);
            GUI.color = colour;
            GUI.DrawTexture(new Rect(at.x - size * 0.5f, at.y - size * 0.5f, size, size), Texture2D.whiteTexture);
        }

        static void Ring(Vector2 at, float size, Color colour)
        {
            // A hollow square reads as a ring at this size and needs no texture.
            GUI.color = colour;
            float t = 3f;
            GUI.DrawTexture(new Rect(at.x - size, at.y - size, size * 2f, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(at.x - size, at.y + size - t, size * 2f, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(at.x - size, at.y - size, t, size * 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(at.x + size - t, at.y - size, t, size * 2f), Texture2D.whiteTexture);
        }

        void Shadowed(Rect rect, string text, Color colour, GUIStyle style = null)
        {
            style ??= _label;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height), text, style);
            GUI.color = colour;
            GUI.Label(rect, text, style);
        }

        void OnDestroy()
        {
            Open = false;
            if (_picture != null) Destroy(_picture);
        }
    }
}
