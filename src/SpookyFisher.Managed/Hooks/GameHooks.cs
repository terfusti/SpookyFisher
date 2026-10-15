using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Terraria;

namespace SpookyFisher
{
    [HarmonyPatch(typeof(Main), "Update", new Type[] { typeof(GameTime) })]
    internal static class UpdateHook
    {
        internal struct InputState
        {
            internal bool Captured, BlockInput, BlockMouse;
        }

        private static void Prefix(out InputState __state)
        {
            __state = default(InputState);
            var runtime = Runtime.Current;
            runtime?.BeforeUpdate();
            if (runtime == null || (!runtime.CapturesMouse && !runtime.CapturesKeyboard)) return;
            __state = new InputState { Captured = true, BlockInput = Main.blockInput, BlockMouse = Main.blockMouse };
            if (runtime.CapturesKeyboard) Main.blockInput = true;
            if (runtime.CapturesMouse) Main.blockMouse = true;
        }

        // A void finalizer leaves Terraria's original exceptions intact.
        private static void Finalizer(InputState __state)
        {
            if (__state.Captured)
            {
                Main.blockInput = __state.BlockInput;
                Main.blockMouse = __state.BlockMouse;
            }
            Runtime.Current?.AfterUpdate();
        }
    }

    [HarmonyPatch(typeof(Main), "Draw", new Type[] { typeof(GameTime) })]
    internal static class DrawHook
    {
        private static void Postfix(Main __instance) => Runtime.Current?.Draw(__instance);
    }

    [HarmonyPatch(typeof(Player), "ItemCheck", new Type[0])]
    internal static class ItemCheckHook
    {
        private static void Prefix(Player __instance, out FishingFeature.TickState __state)
        {
            __state = default(FishingFeature.TickState);
            var runtime = Runtime.Current;
            if (runtime == null || !runtime.Running || Main.netMode == 2 ||
                __instance.whoAmI != Main.myPlayer) return;
            runtime.Fishing.BeforeItemCheck(__instance, out __state, runtime.CapturesMouse || runtime.CapturesKeyboard);
        }

        private static void Postfix(Player __instance, FishingFeature.TickState __state)
        {
            var runtime = Runtime.Current;
            if (runtime != null && runtime.Running)
                runtime.Fishing.AfterItemCheck(__instance, __state);
        }

        private static void Finalizer(Player __instance, FishingFeature.TickState __state)
        {
            FishingFeature.RestoreInput(__instance, __state);
        }
    }
}
