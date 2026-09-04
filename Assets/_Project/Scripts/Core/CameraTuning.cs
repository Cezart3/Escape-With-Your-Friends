namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// The clip planes, in one place, because two different things set them.
    ///
    /// The scene camera in Bootstrap has one pair and every player's Cinemachine camera has another,
    /// and Cinemachine's lens wins whenever a virtual camera is live. Two defaults nobody chose is
    /// how a build ends up rendering a different distance depending on whether a player has spawned
    /// yet, so both read these.
    ///
    /// The far plane is past the water's horizon ring on purpose. Nothing is drawn out there but the
    /// ring itself, the fog is opaque long before it, and clipping the sea in half at some arbitrary
    /// distance would put a seam on the horizon for no saving at all - the cost of distant geometry
    /// is the geometry, not the far plane. <see cref="RenderTuning"/> checks the three distances
    /// still agree.
    /// </summary>
    public static class CameraTuning
    {
        /// <summary>Close enough that a wall you are pressed against does not vanish.</summary>
        public const float NearPlane = 0.15f;

        /// <summary>Past the four-kilometre water ring, so the sea meets the sky rather than an edge.</summary>
        public const float FarPlane = 5000f;
    }
}
