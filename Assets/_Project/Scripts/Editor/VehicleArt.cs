using System.Collections.Generic;
using EscapeWithYourFriends.World;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// #249. The vehicles' own models (tools/art/vehicles.py), worn by the builders. Every mesh is
    /// drawn in metres in its prefab's space, so wearing one is a child at the origin, unscaled.
    /// </summary>
    static class VehicleArt
    {
        internal const string ModelsPath = "Assets/_Project/Art/Casino/Models/Vehicles.fbx";

        static Dictionary<string, Mesh> _models;

        /// <summary>
        /// The <c>Art</c> child BoatTest and FlightTest look for: an <see cref="ArtVisual"/> over
        /// <c>Veh_id</c>. Null, and the greybox left alone, when the model has not been exported.
        /// </summary>
        internal static Transform Art(Transform root, string id)
        {
            GameObject art = Wear(root, id, "Art");
            if (art == null) return null;

            var visual = art.AddComponent<ArtVisual>();
            visual.Id = id;
            visual.Category = ArtCategory.Vehicle;
            return art.transform;
        }

        /// <summary>
        /// <c>Veh_id</c> under a stretched, unrotated box, drawn in the root's space anyway: the holder
        /// undoes the box's scale and offset. The box keeps its place, so whatever turns it (the
        /// propeller) or hides it (PlaneAssembly) takes the model along.
        /// </summary>
        internal static void InRootSpace(Transform box, string id)
        {
            GameObject model = Wear(box, id, "Model");
            if (model == null) return;

            Vector3 s = box.localScale, p = box.localPosition;
            model.transform.localScale = new Vector3(1f / s.x, 1f / s.y, 1f / s.z);
            model.transform.localPosition = new Vector3(-p.x / s.x, -p.y / s.y, -p.z / s.z);
        }

        internal static GameObject Wear(Transform parent, string id, string name = "Model")
        {
            _models ??= SlotFactory.Models(ModelsPath);
            if (_models == null || !_models.TryGetValue("Veh_" + id, out Mesh mesh))
            {
                Debug.LogError($"[VehicleArt] No Veh_{id} in {ModelsPath}: run tools/art/vehicles.py.");
                _models = null;
                return null;
            }

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = SlotFactory.Atlas();
            return go;
        }
    }
}
