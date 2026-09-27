using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// The 52 card faces, drawn in code into one small atlas the first time a card turns up.
    ///
    /// **Why pixels and not a font.** The HUD's font is a canvas thing and the felt is not a
    /// canvas; a texture per card would be 52 materials. One 468x216 atlas and one material, with
    /// each card a quad whose UVs sit on its cell, is the slot screen's trick again: a card is a mesh
    /// swap, the whole table adds one material, and point filtering makes the chunky pixels part of
    /// the low-poly look rather than a blur. Nothing is checked in, so nothing needs regenerating.
    ///
    /// **Layout.** The seats fan their cards so each one covers all but the strip on the player's
    /// right of the one under it, so the index - rank over suit - lives in that strip, top right as
    /// the player sees it. The big rank and suit in the middle are for the dealer's row and the last
    /// card of a hand, which nothing covers.
    /// </summary>
    public static class CardFaces
    {
        public const int CellW = 36;
        public const int CellH = 54;
        public const int Width = CellW * 13;
        public const int Height = CellH * 4;

        /// <summary>Strip on the player's right that stays visible under the next card: a third of the width.</summary>
        const int Strip = 13;

        static readonly Color32 Paper = new(245, 240, 228, 255);
        static readonly Color32 Edge = new(170, 160, 150, 255);
        internal static readonly Color32 Red = new(200, 30, 35, 255);
        internal static readonly Color32 Black = new(25, 25, 30, 255);

        // Glyphs as rows of '#', top row first. Variable width.
        static readonly string[][] Ranks =
        {
            new[] { ".#.", "#.#", "###", "#.#", "#.#" },
            new[] { "##.", "..#", ".#.", "#..", "###" },
            new[] { "##.", "..#", ".#.", "..#", "##." },
            new[] { "#.#", "#.#", "###", "..#", "..#" },
            new[] { "###", "#..", "##.", "..#", "##." },
            new[] { ".##", "#..", "###", "#.#", "###" },
            new[] { "###", "..#", ".#.", ".#.", ".#." },
            new[] { "###", "#.#", "###", "#.#", "###" },
            new[] { "###", "#.#", "###", "..#", "##." },
            // Ten: a narrow one, so "10" fits the index strip at twice size.
            new[] { ".#.###", "##.#.#", ".#.#.#", ".#.#.#", ".#.###" },
            new[] { "..#", "..#", "..#", "#.#", ".#." },
            new[] { ".#.", "#.#", "#.#", "##.", ".##" },
            new[] { "#.#", "##.", "#..", "##.", "#.#" },
        };

        // Spade, heart, diamond, club: BlackjackMath's suit order.
        static readonly string[][] Suits =
        {
            new[] { "...#...", "..###..", ".#####.", "#######", "#######", "...#...", "..###.." },
            new[] { ".##.##.", "#######", "#######", ".#####.", "..###..", "...#...", "......." },
            new[] { "...#...", "..###..", ".#####.", "#######", ".#####.", "..###..", "...#..." },
            new[] { "..###..", "..###..", "#######", "#######", "##.#.##", "...#...", "..###.." },
        };

        static Texture2D _atlas;
        static Material _material;
        static readonly Mesh[] Meshes = new Mesh[52];

        /// <summary>
        /// The atlas as pixels, bottom row first as Unity wants them. Pure, so the harness reads it
        /// headless; <see cref="Atlas"/> is these pixels on a texture.
        /// </summary>
        public static Color32[] Pixels()
        {
            var pixels = new Color32[Width * Height];

            for (int card = 0; card < 52; card++)
            {
                int x0 = BlackjackMath.Rank(card) * CellW;
                int y0 = BlackjackMath.Suit(card) * CellH;
                Color32 ink = BlackjackMath.IsRed(card) ? Red : Black;
                string[] rank = Ranks[BlackjackMath.Rank(card)];
                string[] suit = Suits[BlackjackMath.Suit(card)];

                for (int y = 0; y < CellH; y++)
                for (int x = 0; x < CellW; x++)
                {
                    bool edge = x == 0 || y == 0 || x == CellW - 1 || y == CellH - 1;
                    Set(pixels, x0 + x, y0 + y, edge ? Edge : Paper);
                }

                // The index, centred in the strip.
                int stripX = CellW - Strip;
                Glyph(pixels, rank, 2, x0 + stripX + (Strip - rank[0].Length * 2) / 2, y0 + 3, ink);
                Glyph(pixels, suit, 1, x0 + stripX + (Strip - 7) / 2, y0 + 15, ink);

                // The middle, centred in what the strip leaves.
                int middle = CellW - Strip;
                Glyph(pixels, rank, 3, x0 + (middle - rank[0].Length * 3) / 2 + 1, y0 + 12, ink);
                Glyph(pixels, suit, 2, x0 + (middle - 14) / 2 + 1, y0 + 32, ink);
            }

            return pixels;
        }

        /// <summary>Draws a glyph with its top-left at (x, y), y counted down from the top of the atlas.</summary>
        static void Glyph(Color32[] pixels, string[] rows, int scale, int x, int y, Color32 ink)
        {
            for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < rows[r].Length; c++)
            {
                if (rows[r][c] != '#') continue;

                for (int dy = 0; dy < scale; dy++)
                for (int dx = 0; dx < scale; dx++)
                    Set(pixels, x + c * scale + dx, y + r * scale + dy, ink);
            }
        }

        /// <summary>Sets a pixel by top-down coordinates.</summary>
        static void Set(Color32[] pixels, int x, int yDown, Color32 colour)
            => pixels[(Height - 1 - yDown) * Width + x] = colour;

        public static Texture2D Atlas
        {
            get
            {
                if (_atlas != null) return _atlas;

                _atlas = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
                {
                    name = "CardFaces",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _atlas.SetPixels32(Pixels());
                _atlas.Apply(false, true);
                return _atlas;
            }
        }

        /// <summary>
        /// The card material: a copy of the felt's own card material wearing the atlas, so it has
        /// the island's shader and light. <c>_BaseMap</c> and <c>_BaseColor</c> are URP Lit's names,
        /// which the stylized shader keeps.
        /// </summary>
        public static Material Material(Material basis)
        {
            if (_material != null) return _material;
            if (basis == null) return null;

            _material = new Material(basis) { name = "CardFaces" };
            _material.mainTexture = Atlas;
            if (_material.HasProperty("_BaseMap")) _material.SetTexture("_BaseMap", Atlas);
            if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", Color.white);
            _material.color = Color.white;
            return _material;
        }

        /// <summary>
        /// A flat quad for one card: -0.5..0.5 in x and z, facing up, UVs on its cell. The player
        /// stands at the table's +z looking toward -z, so the card's top is -z and its right is -x.
        /// </summary>
        public static Mesh Mesh(int card)
        {
            card = Mathf.Clamp(card, 0, 51);
            if (Meshes[card] != null) return Meshes[card];

            float u0 = BlackjackMath.Rank(card) * CellW / (float)Width;
            float u1 = u0 + CellW / (float)Width;
            float v1 = 1f - BlackjackMath.Suit(card) * CellH / (float)Height;
            float v0 = v1 - CellH / (float)Height;

            var mesh = new Mesh { name = $"Card {BlackjackMath.Name(card)}" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f),
            };
            mesh.uv = new[] { new Vector2(u1, v1), new Vector2(u0, v1), new Vector2(u0, v0), new Vector2(u1, v0) };
            mesh.triangles = new[] { 0, 3, 2, 0, 2, 1 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            Meshes[card] = mesh;
            return mesh;
        }
    }
}
