using System;
using System.Reflection;
using Terraria;
using Terraria.GameInput;

namespace SpookyFisher
{
    internal sealed class FishingFeature
    {
        // Counts are game updates, normally about 60 per second.
        private const int RecastDelay = 30;
        private const int CastTimeout = 120;
        private const int MaxFailedCasts = 3;
        private MethodInfo pullBobbers;
        private bool enabled, pendingCast, waitingForCast, manualCastPending;
        private Player fishingPlayer;
        private int rodSlot, rodType, idleTicks, castTicks, aimTicks, failedCasts;
        private float aimWorldX, aimWorldY, startingX, startingY;
        private float manualAimX, manualAimY;
        private int retrievals;
        private string status = "Click Start, then cast once into the water.";

        internal bool Enabled => enabled || waitingForCast;

        internal NativeUi.FishingState GetUiState(Player player, out string detail, out string rodName)
        {
            Item rod = player != null && player.selectedItem >= 0 && player.selectedItem < player.inventory.Length
                ? player.inventory[player.selectedItem] : null;
            int bait = player == null ? 0 : player.GetFishingConditions().BaitPower;
            string blocked = BlockedReason(player, !Enabled);
            bool ready = blocked == null && rod != null && rod.fishingPole > 0 && bait > 0;
            rodName = rod != null && rod.fishingPole > 0 ? rod.Name : "No fishing rod equipped";
            if (blocked != null) detail = Enabled ? "Paused: " + blocked : blocked;
            else if (rod == null || rod.fishingPole <= 0) detail = "Hold a fishing rod in your selected slot.";
            else if (bait <= 0) detail = "Add usable bait to your inventory.";
            else detail = status;
            Projectile unused;
            return new NativeUi.FishingState
            {
                Enabled = waitingForCast ? 2 : enabled ? 1 : 0, CanStart = ready ? 1 : 0, HasAim = enabled ? 1 : 0,
                Bobbers = player == null ? 0 : BobberCount(player, out unused), BaitPower = bait,
                FailedCasts = failedCasts, Retrievals = retrievals,
                AimX = aimWorldX, AimY = aimWorldY
            };
        }

        internal struct TickState
        {
            internal bool InputChanged, AimChanged, AllowFishing;
            internal bool SavedUse, SavedRelease;
            internal int MouseX, MouseY, InputX, InputY;
        }

        internal void Initialize()
        {
            pullBobbers = typeof(Player).GetMethod("ItemCheck_PullFishingBobbers",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(Item) }, null);
            if (pullBobbers == null)
                throw new MissingMethodException("Player.ItemCheck_PullFishingBobbers");
            Disable(null);
        }

        internal void Toggle(Player player)
        {
            if (Enabled) { Disable("AFK OFF."); return; }
            if (!CanEnable(player, false)) return;
            EnableAt(player, Main.screenPosition.X + Main.mouseX, Main.screenPosition.Y +
                (player.gravDir == -1f ? Main.screenHeight - Main.mouseY : Main.mouseY));
        }

        internal void Arm(Player player)
        {
            if (Enabled || !CanEnable(player, true)) return;
            fishingPlayer = player;
            rodSlot = player.selectedItem;
            rodType = player.inventory[rodSlot].type;
            manualCastPending = false;
            waitingForCast = true;
            retrievals = failedCasts = castTicks = 0;
            status = "Ready for your first cast. Aim at water and cast once; auto fishing will take over.";
            Log.Write("AFK armed. Waiting for a manual cast to save the aim.");
        }

        private bool CanEnable(Player player, bool fromMenu)
        {
            if (BlockedReason(player, fromMenu) != null || player.selectedItem < 0 || player.selectedItem >= player.inventory.Length)
            {
                status = "Cannot enable here. Close inventory/chat and hold a fishing rod in a world.";
                Log.Write(status);
                return false;
            }
            Item rod = player.inventory[player.selectedItem];
            if (rod.fishingPole <= 0 || player.GetFishingConditions().BaitPower <= 0)
            {
                status = "Cannot enable: a fishing rod and usable bait are required.";
                Log.Write(status);
                return false;
            }
            return true;
        }

        private void EnableAt(Player player, float worldX, float worldY)
        {
            fishingPlayer = player;
            rodSlot = player.selectedItem;
            rodType = player.inventory[rodSlot].type;
            startingX = player.position.X;
            startingY = player.position.Y;
            aimWorldX = worldX;
            aimWorldY = worldY;
            idleTicks = castTicks = aimTicks = failedCasts = 0;
            retrievals = 0;
            pendingCast = waitingForCast = manualCastPending = false;
            enabled = true;
            status = "Waiting to cast toward saved aim.";
            Log.Write("AFK ON. Saved aim at world (" + (int)aimWorldX + ", " + (int)aimWorldY + ").");
        }

        internal void Disable(string reason)
        {
            enabled = pendingCast = waitingForCast = manualCastPending = false;
            fishingPlayer = null;
            idleTicks = castTicks = aimTicks = 0;
            if (reason != null) { status = reason; Log.Write(reason); }
        }

        private static bool Blocked(Player player)
        {
            return BlockedReason(player) != null;
        }

        private static string BlockedReason(Player player, bool ignoreMouseInterface = false)
        {
            if (Main.gameMenu || player == null || !player.active || player.dead) return "Enter a world with a living player.";
            if (Main.gamePaused) return "Game is paused.";
            if (Main.playerInventory) return "Close the inventory to fish.";
            if (Main.drawingPlayerChat) return "Close chat to fish.";
            if (Main.blockInput) return "Game input is blocked.";
            if (player.CCed || player.noItems || player.pulley) return "Player cannot use a fishing rod here.";
            if (!ignoreMouseInterface && player.mouseInterface) return "Move the cursor away from Terraria's interface.";
            if (Main.mouseItem != null && !Main.mouseItem.IsAir) return "Put away the item held by the cursor.";
            return null;
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

        private void ApplyAim(Player player, ref TickState state)
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

        internal void BeforeItemCheck(Player player, out TickState state, bool uiCapturesInput = false)
        {
            state = new TickState();
            try
            {
                if (!Enabled) return;

                if (!ReferenceEquals(player, fishingPlayer) || player.dead ||
                    player.selectedItem != rodSlot || player.selectedItem < 0 ||
                    player.selectedItem >= player.inventory.Length || player.inventory[player.selectedItem].type != rodType)
                {
                    Disable("AFK OFF: player or selected rod changed.");
                    return;
                }
                if (waitingForCast)
                {
                    if (!Blocked(player) && player.GetFishingConditions().BaitPower <= 0)
                    {
                        Disable("AFK OFF: no usable bait remains.");
                        return;
                    }
                    Projectile unused;
                    if (!manualCastPending && !uiCapturesInput && !Blocked(player) &&
                        player.controlUseItem && player.itemAnimation == 0 && player.itemTime == 0 &&
                        player.reuseDelay == 0 && BobberCount(player, out unused) == 0)
                    {
                        manualAimX = Main.screenPosition.X + Main.mouseX;
                        manualAimY = Main.screenPosition.Y +
                            (player.gravDir == -1f ? Main.screenHeight - Main.mouseY : Main.mouseY);
                        manualCastPending = true;
                        castTicks = 0;
                    }
                    if (manualCastPending && ++castTicks >= CastTimeout)
                    {
                        manualCastPending = false;
                        status = "No bobber appeared. Cast again to choose your fishing location.";
                    }
                    return; // Arming never synthesizes input or moves the cursor.
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
                if (player.controlUseItem || player.controlUseTile)
                {
                    status = "Paused: manual item use.";
                    return;
                }
                state.AllowFishing = true;

                Projectile ready;
                int count = BobberCount(player, out ready);
                if (pendingCast)
                {
                    if (count > 0)
                    {
                        pendingCast = false;
                        failedCasts = 0;
                        status = "Bobber cast. Waiting for a bite.";
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
                        status = "No bobber appeared. Retrying cast.";
                    }
                }
                if (aimTicks > 0)
                {
                    ApplyAim(player, ref state);
                    aimTicks--;
                }
                if (count > 0)
                {
                    idleTicks = 0;
                    status = "Waiting for a bite / bobbers to return.";
                    return;
                }
                if (pendingCast || player.itemAnimation != 0 || player.itemTime != 0 || player.reuseDelay != 0)
                    return;
                if (++idleTicks < RecastDelay) { status = "Recast delay. Waiting to cast."; return; }

                Item rod = player.inventory[player.selectedItem];
                // One update of normal item-use input. Terraria performs the actual cast.
                state.InputChanged = true;
                state.SavedUse = player.controlUseItem;
                state.SavedRelease = player.releaseUseItem;
                player.controlUseItem = true;
                player.releaseUseItem = true;
                aimTicks = Math.Max(rod.useAnimation, rod.useTime) + 5;
                ApplyAim(player, ref state);
                pendingCast = true;
                castTicks = idleTicks = 0;
                status = "Casting toward saved aim.";
                Log.Write("Casting toward saved aim.");
            }
            catch (Exception ex) { Disable("AFK OFF after an error: " + ex); }
        }

        internal void AfterItemCheck(Player player, TickState state)
        {
            if (waitingForCast && manualCastPending && ReferenceEquals(player, fishingPlayer))
            {
                Projectile unused;
                if (BobberCount(player, out unused) > 0)
                {
                    EnableAt(player, manualAimX, manualAimY);
                    status = "First cast saved. Waiting for a bite.";
                }
                return;
            }
            if (!enabled ||
                !state.AllowFishing || player.itemAnimation != 0) return;
            try
            {
                Projectile ready;
                BobberCount(player, out ready);
                if (ready == null) return;
                int catchId = (int)ready.localAI[1];
                pullBobbers.Invoke(player, new object[] { player.inventory[player.selectedItem] });
                retrievals++;
                status = "Reeling in item catch " + catchId + ".";
                Log.Write("Retrieval requested for item catch " + catchId + ". Waiting for bobbers to return.");
            }
            catch (Exception ex) { Disable("AFK OFF after retrieval error: " + ex); }
        }

        // Runs even when original item processing throws; restores only our temporary input.
        // A void finalizer preserves any exception from Terraria instead of swallowing it.
        internal static void RestoreInput(Player player, TickState state)
        {
            if (state.InputChanged)
            {
                player.controlUseItem = state.SavedUse;
                player.releaseUseItem = state.SavedRelease;
            }
            if (state.AimChanged)
            {
                Main.mouseX = state.MouseX;
                Main.mouseY = state.MouseY;
                PlayerInput.MouseX = state.InputX;
                PlayerInput.MouseY = state.InputY;
            }
        }
    }
}
