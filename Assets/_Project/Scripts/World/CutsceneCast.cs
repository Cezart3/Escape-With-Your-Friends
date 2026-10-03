using System.Linq;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// Everything a cutscene puts on screen that the scene does not already have (#287): the four
    /// friends' bodies, Bogdan's, the animation library and the sets drawn in tools/art/sets.py.
    /// Built by <c>CutsceneCastBuilder</c> into Resources, so a scene loads it by name and nothing in a
    /// .unity file has to point at it.
    /// </summary>
    public class CutsceneCast : ScriptableObject
    {
        public const string ResourceName = "CutsceneCast";

        [Tooltip("Gus, Kiki, Mo and Rex, in that order: the Body_Player_* models.")]
        public GameObject[] Friends;

        [Tooltip("Bogdan, the Marisol's captain: Body_Castaway.")]
        public GameObject Bogdan;

        [Tooltip("The whole humanoid library. Scenes ask for a clip by name.")]
        public AnimationClip[] Clips;

        [Tooltip("Set_* meshes from Sets.fbx.")]
        public Mesh[] Sets;

        [Tooltip("The ramp-sheet material the sets are painted from.")]
        public Material Atlas;

        static CutsceneCast _loaded;

        public static CutsceneCast Load() => _loaded != null ? _loaded : _loaded = Resources.Load<CutsceneCast>(ResourceName);

        /// <summary>A clip by its library name ("Walk_Loop"), ignoring case, underscores and a rig prefix.</summary>
        public AnimationClip Clip(string name)
        {
            string wanted = Plain(name);
            return Clips.FirstOrDefault(c => c != null && Plain(c.name) == wanted)
                   ?? Clips.FirstOrDefault(c => c != null && Plain(c.name).EndsWith(wanted));
        }

        public Mesh Set(string name) => Sets.FirstOrDefault(m => m != null && m.name == "Set_" + name);

        static string Plain(string text) => new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
