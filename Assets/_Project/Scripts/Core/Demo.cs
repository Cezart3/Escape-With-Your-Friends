namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// The demo (#90): the first island and nothing past it. Punching, carrying, fishing, the shop and
    /// the table are all on that island already, so the demo is not a cut of the content - it is one
    /// gate. <c>GameSceneLoader.ServerTravel</c> is the only way off an island, by boat or by plane,
    /// and in the demo it ends the run instead, onto a panel that says what comes next.
    ///
    /// It also keeps no save. A demo sharing a save folder with the full game would otherwise sail a
    /// full-game save to the second island, which in the demo means ending it on the first frame.
    ///
    /// A demo build is compiled with <c>EWYF_DEMO</c> (<c>BuildTool -demo</c>), so there is no flag to
    /// remove. <c>-demo</c> turns it on in any build, which is how the harness sees it.
    /// </summary>
    public static class Demo
    {
#if EWYF_DEMO
        const bool Built = true;
#else
        const bool Built = false;
#endif

        public static readonly bool On = Built || CommandLine.HasFlag("-demo");
    }
}
