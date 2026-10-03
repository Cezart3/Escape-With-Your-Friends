using System.Linq;
using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Writes Resources/CutsceneCast.asset (#287): the friends' and Bogdan's bodies, every clip in the
    /// animation library and the sets from tools/art/sets.py. Run after characters.py or sets.py:
    ///
    ///     Unity -batchmode -quit -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.CutsceneCastBuilder.Build
    /// </summary>
    public static class CutsceneCastBuilder
    {
        const string AssetPath = "Assets/_Project/Resources/" + CutsceneCast.ResourceName + ".asset";
        const string SetsPath = "Assets/_Project/Art/Casino/Models/Sets.fbx";

        [MenuItem("EWYF/Build Cutscene Cast")]
        public static void Build()
        {
            var cast = AssetDatabase.LoadAssetAtPath<CutsceneCast>(AssetPath);
            if (cast == null)
            {
                cast = ScriptableObject.CreateInstance<CutsceneCast>();
                AssetDatabase.CreateAsset(cast, AssetPath);
            }

            cast.Friends = CharacterArt.Bodies(CharacterArt.Players);
            cast.Bogdan = CharacterArt.Bodies(CharacterArt.Castaway).FirstOrDefault();
            cast.Radu = CharacterArt.Bodies(CharacterArt.Pilot).FirstOrDefault();
            cast.Clips = CharacterArt.Clips().Values.Distinct().OrderBy(c => c.name).ToArray();
            cast.Sets = (SlotFactory.Models(SetsPath)?.Values ?? Enumerable.Empty<Mesh>()).OrderBy(m => m.name).ToArray();
            cast.Atlas = SlotFactory.Atlas();
            EditorUtility.SetDirty(cast);
            AssetDatabase.SaveAssets();

            bool ok = cast.Friends.Length == 4 && cast.Bogdan != null && cast.Radu != null && cast.Clips.Length > 30 && cast.Sets.Length >= 8
                      && cast.Atlas != null;
            Debug.Log($"[CutsceneCast] {(ok ? "Done" : "FAILED")}: friends {string.Join(", ", cast.Friends.Select(f => f.name))}; "
                      + $"Bogdan {(cast.Bogdan != null ? cast.Bogdan.name : "missing")}; "
                      + $"Radu {(cast.Radu != null ? cast.Radu.name : "missing")}; {cast.Clips.Length} clips; "
                      + $"sets {string.Join(", ", cast.Sets.Select(m => m.name))}.");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
