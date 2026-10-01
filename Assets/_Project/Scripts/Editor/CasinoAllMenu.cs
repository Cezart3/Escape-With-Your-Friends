using EscapeWithYourFriends.Casino;
using UnityEditor;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// <c>-casinoAll</c> for Play mode, where there is no command line: every casino game open on
    /// day 1 and the VIP door free. Ticked, it holds until unticked, and takes effect on the next
    /// press of Play.
    /// </summary>
    static class CasinoAllMenu
    {
        const string Item = "EWYF/Casino: open everything";

        [MenuItem(Item)]
        static void Toggle()
        {
            bool on = !EditorPrefs.GetBool(CasinoDays.AllOpenPref);
            EditorPrefs.SetBool(CasinoDays.AllOpenPref, on);
        }

        [MenuItem(Item, true)]
        static bool Validate()
        {
            Menu.SetChecked(Item, EditorPrefs.GetBool(CasinoDays.AllOpenPref));
            return true;
        }
    }
}
