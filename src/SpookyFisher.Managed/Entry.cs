using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Terraria;
using Terraria.GameInput;

namespace SpookyFisher
{
    public static class Entry
    {
        internal const string HarmonyId = "terfusti.spookyfisher";
        private static Harmony harmony;
        private static EventWaitHandle toggleEvent, stopEvent, detachedEvent;
        internal static bool Attached { get; private set; }
        internal static bool DetachRequested { get; private set; }

        public static int Start(string eventPrefix)
        {
            try
            {
                if (Attached) return 0;
                if (string.IsNullOrWhiteSpace(eventPrefix))
                    throw new ArgumentException("Use the updated native loader; control events are required.");

                toggleEvent = EventWaitHandle.OpenExisting(eventPrefix + ".Toggle");
                stopEvent = EventWaitHandle.OpenExisting(eventPrefix + ".Stop");
                detachedEvent = EventWaitHandle.OpenExisting(eventPrefix + ".Detached");
                FishingPatch.Initialize();
                DetachRequested = false;
                harmony = new Harmony(HarmonyId);
                Attached = true;
                harmony.PatchAll(typeof(Entry).Assembly);
                Log.Write("Managed hook installed. AFK is OFF. F6: toggle; F7: detach.");
                return 0;
            }
            catch (Exception ex)
            {
                Attached = false;
                Log.Write("Initialization failed: " + ex);
                try { if (harmony != null) harmony.UnpatchAll(HarmonyId); }
                catch (Exception cleanupError) { Log.Write("Patch cleanup failed: " + cleanupError); }
                harmony = null;
                CloseEvents();
                return 1;
            }
        }

        // Called exclusively from the game's ItemCheck hook.
        internal static void PollControls(Player player)
        {
            if (stopEvent.WaitOne(0))
            {
                DetachRequested = true;
                FishingPatch.Disable("Detach requested.");
                return;
            }
            if (toggleEvent.WaitOne(0)) FishingPatch.Toggle(player);
        }

        internal static void DetachOnGameThread()
        {
            if (!Attached) return;
            try
            {
                FishingPatch.Disable(null);
                // Explicit ID: do not remove other mods' Harmony patches.
                harmony.UnpatchAll(HarmonyId);
                harmony = null;
                Attached = false;
                DetachRequested = false;
                Log.Write("Fishing hook removed. Native bridge may now unload.");
                toggleEvent.Dispose();
                toggleEvent = null;
                stopEvent.Dispose();
                stopEvent = null;
                // Signal only after unpatching and completing all console/file logging.
                detachedEvent.Set();
                detachedEvent.Dispose();
                detachedEvent = null;
            }
            catch (Exception ex)
            {
                // No acknowledgement on failure: the native bridge stays loaded.
                Log.Write("Detach failed; bridge remains loaded: " + ex);
            }
        }

        private static void CloseEvents()
        {
            if (toggleEvent != null) toggleEvent.Dispose();
            if (stopEvent != null) stopEvent.Dispose();
            if (detachedEvent != null) detachedEvent.Dispose();
            toggleEvent = stopEvent = detachedEvent = null;
        }
    }

    [HarmonyPatch(typeof(Player), "ItemCheck", new Type[0])]
    internal static class FishingPatch
    {
        // Counts are game updates, normally about 60 per second.
        private const int RecastDelay = 30;
        private const int CastTimeout = 120;
        private const int MaxFailedCasts = 3;
        private static MethodInfo pullBobbers;
        private static bool enabled, pendingCast;
        private static Player fishingPlayer;
        private static int rodSlot, rodType, idleTicks, castTicks, aimTicks, failedCasts;
        private static float aimWorldX, aimWorldY, startingX, startingY;

        internal sealed class TickState
        {
            internal bool InputChanged, AimChanged, AllowFishing;
            internal bool SavedUse, SavedRelease;
            internal int MouseX, MouseY, InputX, InputY;
        }

        internal static void Initialize()
        {
            pullBobbers = typeof(Player).GetMethod("ItemCheck_PullFishingBobbers",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(Item) }, null);
            if (pullBobbers == null)
                throw new MissingMethodException("Player.ItemCheck_PullFishingBobbers");
            Disable(null);
        }

        internal static void Toggle(Player player)
        {
            if (enabled) { Disable("AFK OFF."); return; }
            if (Blocked(player) || player.selectedItem < 0 || player.selectedItem >= player.inventory.Length)
            {
                Log.Write("Cannot enable here. Close inventory/chat and hold a fishing rod in a world.");
                return;
            }
            Item rod = player.inventory[player.selectedItem];
            if (rod.fishingPole <= 0 || player.GetFishingConditions().BaitPower <= 0)
            {
                Log.Write("Cannot enable: a fishing rod and usable bait are required.");
                return;
            }
            fishingPlayer = player;
            rodSlot = player.selectedItem;
            rodType = rod.type;
            startingX = player.position.X;
            startingY = player.position.Y;
            aimWorldX = Main.screenPosition.X + Main.mouseX;
            aimWorldY = Main.screenPosition.Y +
                (player.gravDir == -1f ? Main.screenHeight - Main.mouseY : Main.mouseY);
            idleTicks = castTicks = aimTicks = failedCasts = 0;
            pendingCast = false;
            enabled = true;
            Log.Write("AFK ON. Saved aim at world (" + (int)aimWorldX + ", " + (int)aimWorldY + ").");
        }

        internal static void Disable(string reason)
        {
            enabled = pendingCast = false;
            idleTicks = castTicks = aimTicks = failedCasts = 0;
            if (reason != null) Log.Write(reason);
        }

        private static bool Blocked(Player player)
        {
            return Main.gameMenu || Main.gamePaused || Main.playerInventory ||
                Main.drawingPlayerChat || !player.active || player.dead ||
                player.CCed || player.noItems || player.pulley || player.mouseInterface ||
                (Main.mouseItem != null && !Main.mouseItem.IsAir);
        }

        private static int BobberCount(Player player, out Projectile ready)
        {
            int count = 0;
            ready = null;
            foreach (Projectile p in Main.projectile)
            {
                if (p == null || !p.active || !p.bobber || p.owner != player.whoAmI) continue;
                count++;
                // Positive localAI[1] selects an item; negative values select NPC catches.
                if (ready == null && p.ai[0] == 0f && p.ai[1] < 0f && p.localAI[1] > 0f)
                    ready = p;
            }
            return count;
        }

        private static void ApplyAim(Player player, TickState state)
        {
            if (!state.AimChanged)
            {
                state.MouseX = Main.mouseX;
                state.MouseY = Main.mouseY;
                state.InputX = PlayerInput.MouseX;
                state.InputY = PlayerInput.MouseY;
                state.AimChanged = true;
            }
            Main.mouseX = PlayerInput.MouseX = (int)Math.Round(aimWorldX - Main.screenPosition.X);
            Main.mouseY = PlayerInput.MouseY = (int)Math.Round(player.gravDir == -1f
                ? Main.screenPosition.Y + Main.screenHeight - aimWorldY
                : aimWorldY - Main.screenPosition.Y);
        }

        private static void Prefix(Player __instance, out TickState __state)
        {
            __state = new TickState();
            Player player = __instance;
            if (!Entry.Attached || Main.netMode == 2 || player.whoAmI != Main.myPlayer) return;
            try
            {
                Entry.PollControls(player);
                if (Entry.DetachRequested || !enabled) return;

                if (!ReferenceEquals(player, fishingPlayer) || player.dead ||
                    player.selectedItem != rodSlot || player.inventory[player.selectedItem].type != rodType)
                {
                    Disable("AFK OFF: player or selected rod changed.");
                    return;
                }
                float dx = player.position.X - startingX, dy = player.position.Y - startingY;
                if (dx * dx + dy * dy > 24f * 24f)
                {
                    Disable("AFK OFF: player moved. Aim and press F6 again.");
                    return;
                }
                if (Blocked(player)) return; // Pause without discarding the toggle/aim.
                if (player.GetFishingConditions().BaitPower <= 0)
                {
                    Disable("AFK OFF: no usable bait remains.");
                    return;
                }
                // Give physical item-use input priority.
                if (player.controlUseItem || player.controlUseTile) return;
                __state.AllowFishing = true;

                Projectile ready;
                int count = BobberCount(player, out ready);
                if (pendingCast)
                {
                    if (count > 0)
                    {
                        pendingCast = false;
                        failedCasts = 0;
                        Log.Write("Bobber cast. Waiting for a bite.");
                    }
                    else if (++castTicks >= CastTimeout)
                    {
                        pendingCast = false;
                        aimTicks = 0;
                        idleTicks = 0;
                        if (++failedCasts >= MaxFailedCasts)
                        {
                            Disable("AFK OFF: three cast attempts produced no bobber.");
                            return;
                        }
                        Log.Write("No bobber appeared; scheduling another cast.");
                    }
                }
                if (aimTicks > 0)
                {
                    ApplyAim(player, __state);
                    aimTicks--;
                }
                if (count > 0) { idleTicks = 0; return; }
                if (pendingCast || player.itemAnimation != 0 || player.itemTime != 0 || player.reuseDelay != 0)
                    return;
                if (++idleTicks < RecastDelay) return;

                Item rod = player.inventory[player.selectedItem];
                // One update of normal item-use input. Terraria performs the actual cast.
                __state.InputChanged = true;
                __state.SavedUse = player.controlUseItem;
                __state.SavedRelease = player.releaseUseItem;
                player.controlUseItem = true;
                player.releaseUseItem = true;
                aimTicks = Math.Max(rod.useAnimation, rod.useTime) + 5;
                ApplyAim(player, __state);
                pendingCast = true;
                castTicks = idleTicks = 0;
                Log.Write("Casting toward saved aim.");
            }
            catch (Exception ex) { Disable("AFK OFF after an error: " + ex); }
        }

        private static void Postfix(Player __instance, TickState __state)
        {
            Player player = __instance;
            if (!Entry.Attached || Entry.DetachRequested || !enabled ||
                __state == null || !__state.AllowFishing || player.itemAnimation != 0) return;
            try
            {
                Projectile ready;
                BobberCount(player, out ready);
                if (ready == null) return;
                int catchId = (int)ready.localAI[1];
                pullBobbers.Invoke(player, new object[] { player.inventory[player.selectedItem] });
                Log.Write("Retrieval requested for item catch " + catchId + ". Waiting for bobbers to return.");
            }
            catch (Exception ex) { Disable("AFK OFF after retrieval error: " + ex); }
        }

        // Runs even when original item processing throws; restores only our temporary input.
        // A void finalizer preserves any exception from Terraria instead of swallowing it.
        private static void Finalizer(Player __instance, TickState __state)
        {
            if (__state != null)
            {
                if (__state.InputChanged)
                {
                    __instance.controlUseItem = __state.SavedUse;
                    __instance.releaseUseItem = __state.SavedRelease;
                }
                if (__state.AimChanged)
                {
                    Main.mouseX = __state.MouseX;
                    Main.mouseY = __state.MouseY;
                    PlayerInput.MouseX = __state.InputX;
                    PlayerInput.MouseY = __state.InputY;
                }
            }
            if (Entry.Attached && Entry.DetachRequested && __instance.whoAmI == Main.myPlayer)
                Entry.DetachOnGameThread();
        }
    }

    internal static class Log
    {
        private static readonly string FilePath = Path.Combine(
            Path.GetDirectoryName(typeof(Entry).Assembly.Location), "SpookyFisher.managed.log");

        internal static void Write(string message)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine;
            try { File.AppendAllText(FilePath, line); } catch { }
            try
            {
                // Open the console output explicitly; don't replace Terraria's Console.Out.
                IntPtr handle = CreateFileW("CONOUT$", 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle == new IntPtr(-1)) return;
                try
                {
                    uint written;
                    WriteConsoleW(handle, line, (uint)line.Length, out written, IntPtr.Zero);
                }
                finally { CloseHandle(handle); }
            }
            catch { }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string path, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WriteConsoleW(IntPtr handle, string text, uint count,
            out uint written, IntPtr reserved);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
