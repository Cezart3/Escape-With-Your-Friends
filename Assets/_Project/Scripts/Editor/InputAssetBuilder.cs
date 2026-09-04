using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Generates the input action asset every player reads from.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.InputAssetBuilder.BuildInputAsset
    ///
    /// A .inputactions file is JSON, so in principle it could be written by hand. It is generated here
    /// anyway because the binding strings are the part that breaks silently: a typo in
    /// "&lt;Gamepad&gt;/leftStick" produces an asset that imports cleanly and simply never fires. Building it
    /// through the API means a bad control path is a compile-or-run error in one place instead of a
    /// mystery in the field.
    ///
    /// One map, "Player". Splitting UI and gameplay maps comes when there is UI to split (#106).
    /// </summary>
    public static class InputAssetBuilder
    {
        /// <summary>Kept equal to <see cref="Items.Inventory.HotbarSlots"/>; one binding per slot.</summary>
        const int HotbarSlots = 5;

        const string InputDir = "Assets/_Project/Input";
        const string AssetPath = InputDir + "/PlayerControls.inputactions";

        public static void BuildInputAsset()
        {
            Directory.CreateDirectory(InputDir);

            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            asset.name = "PlayerControls";

            InputActionMap map = asset.AddActionMap("Player");

            // Value, not Button: the motor wants the axis every tick, not an edge.
            InputAction move = map.AddAction("Move", InputActionType.Value, expectedControlLayout: "Vector2");
            move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            move.AddBinding("<Gamepad>/leftStick");

            // Mouse delta is already a per-frame delta, so no processor here; sensitivity is applied by
            // the reader, where a player can eventually change it.
            InputAction look = map.AddAction("Look", InputActionType.Value, expectedControlLayout: "Vector2");
            look.AddBinding("<Mouse>/delta");
            look.AddBinding("<Gamepad>/rightStick").WithProcessor("scaleVector2(x=8,y=8)");

            InputAction jump = map.AddAction("Jump", InputActionType.Button);
            jump.AddBinding("<Keyboard>/space");
            jump.AddBinding("<Gamepad>/buttonSouth");

            // Held, not toggled. A sprint toggle needs its own replicated bit; a held key is already
            // one, and holding shift is what everyone expects.
            InputAction sprint = map.AddAction("Sprint", InputActionType.Button);
            sprint.AddBinding("<Keyboard>/leftShift");
            sprint.AddBinding("<Gamepad>/leftStickPress");

            InputAction crouch = map.AddAction("Crouch", InputActionType.Button);
            crouch.AddBinding("<Keyboard>/leftCtrl");
            crouch.AddBinding("<Gamepad>/buttonEast");

            InputAction interact = map.AddAction("Interact", InputActionType.Button);
            interact.AddBinding("<Keyboard>/e");
            interact.AddBinding("<Gamepad>/buttonWest");

            InputAction attack = map.AddAction("Attack", InputActionType.Button);
            attack.AddBinding("<Mouse>/leftButton");
            attack.AddBinding("<Gamepad>/rightTrigger");

            // Second fire: taser now, whatever the held weapon offers later.
            InputAction altAttack = map.AddAction("AltAttack", InputActionType.Button);
            altAttack.AddBinding("<Mouse>/rightButton");
            altAttack.AddBinding("<Gamepad>/leftTrigger");

            // Tap to drop, hold to throw. One key for two verbs because the hold reads as winding up,
            // and because a separate throw key is a binding nobody would find.
            InputAction drop = map.AddAction("Drop", InputActionType.Button);
            drop.AddBinding("<Keyboard>/g");
            drop.AddBinding("<Gamepad>/buttonNorth");

            // Hotbar selection. Five slots is what fits across a screen without a second row, and it
            // is what the drop key acts on until the real hotbar UI lands in #46.
            for (int slot = 1; slot <= HotbarSlots; slot++)
            {
                InputAction pick = map.AddAction($"Hotbar{slot}", InputActionType.Button);
                pick.AddBinding($"<Keyboard>/{slot}");
            }

            // A Value action rather than a Button: the reader wants the notch count, and it
            // accumulates fractions so a trackpad works as well as a wheel.
            InputAction hotbarScroll = map.AddAction("HotbarScroll", InputActionType.Value);
            hotbarScroll.expectedControlType = "Axis";
            hotbarScroll.AddBinding("<Mouse>/scroll/y");
            hotbarScroll.AddBinding("<Gamepad>/dpad/x");

            File.WriteAllText(AssetPath, asset.ToJson());
            Object.DestroyImmediate(asset);

            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();

            var imported = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            if (imported == null)
            {
                Debug.LogError($"[InputAssetBuilder] {AssetPath} was written but did not import.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            Debug.Log($"[InputAssetBuilder] {AssetPath}: {imported.actionMaps.Count} map(s), "
                      + $"{imported.FindActionMap("Player", throwIfNotFound: true).actions.Count} actions.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
