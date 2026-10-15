using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;

namespace SpookyFisher
{
    [HarmonyPatch(typeof(PlayerInput), "MouseInput")]
    internal static class MenuMouseInputHook
    {
        private static void Postfix()
        {
            if (Runtime.Current?.CapturesMouse != true) return;
            // Keep sampling coordinates/wheel, but remove mouse bindings before KeyboardInput
            // merges them with keyboard bindings. This also respects remapped mouse buttons.
            PlayerInput.MouseKeys.Clear();
            PlayerInput.ScrollWheelValueOld = PlayerInput.ScrollWheelValue;
        }
    }

    [HarmonyPatch(typeof(PlayerInput), "UpdateInput")]
    internal static class MenuInputHook
    {
        private static void Postfix()
        {
            var runtime = Runtime.Current;
            if (runtime?.CapturesKeyboard == true)
            {
                PlayerInput.Triggers.Reset();
                Main.keyState = Main.oldKeyState = default(KeyboardState);
            }
            if (runtime?.CapturesMouse == true)
            {
                PlayerInput.ScrollWheelDelta = PlayerInput.ScrollWheelDeltaForUI = 0;
                Main.mouseLeft = Main.mouseRight = false;
                Main.mouseLeftRelease = Main.mouseRightRelease = false;
            }
        }
    }

    [HarmonyPatch]
    internal static class MenuTextHook
    {
        private static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(Main))
            .Where(method => method.Name == "GetInputText" && method.ReturnType == typeof(string) &&
                method.GetParameters().Length > 0 && method.GetParameters()[0].ParameterType == typeof(string));

        private static bool Prefix(string __0, ref string __result)
        {
            if (Runtime.Current?.CapturesKeyboard != true) return true;
            __result = __0;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class MenuCursorHook
    {
        private static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(Main))
            .Where(method => method.Name == "DrawCursor" || method.Name == "DrawThickCursor");

        private static bool Prefix() => Runtime.Current?.CapturesMouse != true;
    }
}
