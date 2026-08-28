namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// Running out of health does not kill you here — it puts you down. Dying is what happens when
    /// nobody comes to get you.
    ///
    /// The gap between the two states is where the game lives: you are on the ground, carryable by
    /// anyone including the natives, and a timer your friends can see is running out.
    /// </summary>
    public enum LifeState : byte
    {
        /// <summary>Upright, animated, in control.</summary>
        Alive = 0,

        /// <summary>
        /// Health hit zero. Ragdolled, carryable, bleeding out on a shared timer. Rescuing from here
        /// is cheap — it is the outcome we want players fighting for.
        /// </summary>
        Downed = 1,

        /// <summary>
        /// The bleed-out timer ran out, or something killed outright. Now it costs money and a trip
        /// to the Revive Machine.
        /// </summary>
        Dead = 2,
    }
}
