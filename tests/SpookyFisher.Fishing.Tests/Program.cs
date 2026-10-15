using System;
using SpookyFisher;
using Terraria;
using Game = Terraria.Main;

internal static class Program
{
    private static int Main()
    {
        Action[] tests = { ArmDoesNotCast, ConfirmFirstCast, IgnorePanelAndExistingBobbers,
            CancelAndRodChanges, MoveBeforeCast, RetryMissingBobber, OtherPlayerCannotConfirm,
            InvertedGravityAndF6, BaitLoss };
        try
        {
            foreach (var test in tests) { Reset(); test(); Console.WriteLine("PASS " + test.Method.Name); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Reset()
    {
        Game.gameMenu = Game.gamePaused = Game.playerInventory = Game.blockInput = Game.drawingPlayerChat = false;
        Game.mouseItem = null;
        Game.screenPosition = new Position { X = 1000, Y = 2000 };
        Game.mouseX = 300; Game.mouseY = 200;
        Game.projectile = new Projectile[0];
    }

    private static FishingFeature Create()
    {
        var feature = new FishingFeature(); feature.Initialize(); return feature;
    }

    private static NativeUi.FishingState Snapshot(FishingFeature feature, Player player)
    {
        string status, rod;
        return feature.GetUiState(player, out status, out rod);
    }

    private static FishingFeature.TickState Before(FishingFeature feature, Player player, bool captured = false)
    {
        FishingFeature.TickState tick;
        feature.BeforeItemCheck(player, out tick, captured);
        return tick;
    }

    private static void ArmDoesNotCast()
    {
        var player = new Player { mouseInterface = true };
        var feature = Create();
        Assert(Snapshot(feature, player).CanStart == 1, "Arming required a prior water aim or rejected the panel position.");
        feature.Arm(player);
        player.mouseInterface = false;
        for (int i = 0; i < 180; i++)
        {
            var tick = Before(feature, player);
            feature.AfterItemCheck(player, tick);
            Assert(!tick.InputChanged && !tick.AimChanged && !player.controlUseItem, "Arming synthesized a cast or changed aim.");
        }
        var snapshot = Snapshot(feature, player);
        Assert(snapshot.Enabled == 2 && snapshot.HasAim == 0, "Arming did not wait indefinitely for a manual cast.");
    }

    private static void ConfirmFirstCast()
    {
        var player = new Player(); var feature = Create(); feature.Arm(player);
        player.controlUseItem = true;
        var tick = Before(feature, player);
        feature.AfterItemCheck(player, tick);
        Assert(Snapshot(feature, player).Enabled == 2, "An item-use attempt was treated as a successful cast.");
        Game.mouseX = 600; Game.mouseY = 500;
        Game.projectile = new[] { new Projectile() };
        feature.AfterItemCheck(player, tick);
        var snapshot = Snapshot(feature, player);
        Assert(snapshot.Enabled == 1 && snapshot.HasAim == 1 && snapshot.AimX == 1300 && snapshot.AimY == 2200,
            "The first confirmed cast did not save its original world aim.");
    }

    private static void IgnorePanelAndExistingBobbers()
    {
        var player = new Player { controlUseItem = true }; var feature = Create(); feature.Arm(player);
        var tick = Before(feature, player, true);
        Game.projectile = new[] { new Projectile() };
        feature.AfterItemCheck(player, tick);
        Assert(Snapshot(feature, player).Enabled == 2, "A panel click chose the cast location.");
        tick = Before(feature, player);
        feature.AfterItemCheck(player, tick);
        Assert(Snapshot(feature, player).Enabled == 2, "Retrieving an existing bobber chose a new cast location.");
    }

    private static void CancelAndRodChanges()
    {
        var player = new Player { controlUseItem = true }; var feature = Create(); feature.Arm(player);
        var tick = Before(feature, player);
        feature.Disable("Cancelled");
        Game.projectile = new[] { new Projectile() };
        feature.AfterItemCheck(player, tick);
        Assert(!feature.Enabled, "A cancelled start was reactivated by a bobber.");
        feature.Arm(player);
        player.inventory[0].type++;
        Before(feature, player);
        Assert(!feature.Enabled, "Changing rods did not cancel an armed start.");
    }

    private static void MoveBeforeCast()
    {
        var player = new Player(); var feature = Create(); feature.Arm(player);
        player.position = new Position { X = 500, Y = 500 };
        Before(feature, player);
        Assert(Snapshot(feature, player).Enabled == 2, "Repositioning before the first cast cancelled the start.");
    }

    private static void RetryMissingBobber()
    {
        var player = new Player { controlUseItem = true }; var feature = Create(); feature.Arm(player);
        Before(feature, player);
        player.controlUseItem = false;
        for (int i = 0; i < 125; i++) Before(feature, player);
        Assert(Snapshot(feature, player).Enabled == 2, "A failed manual cast prevented another attempt.");
        Game.mouseX = 450; player.controlUseItem = true;
        var tick = Before(feature, player);
        Game.projectile = new[] { new Projectile() };
        feature.AfterItemCheck(player, tick);
        Assert(Snapshot(feature, player).AimX == 1450, "Retry kept the failed cast's aim.");
    }

    private static void OtherPlayerCannotConfirm()
    {
        var player = new Player { controlUseItem = true }; var feature = Create(); feature.Arm(player);
        var tick = Before(feature, player);
        var other = new Player { whoAmI = 1 };
        Game.projectile = new[] { new Projectile { owner = 1 } };
        feature.AfterItemCheck(other, tick);
        Assert(Snapshot(feature, player).Enabled == 2, "Another player's cast activated local auto fishing.");
    }

    private static void InvertedGravityAndF6()
    {
        var player = new Player { gravDir = -1 }; var feature = Create();
        feature.Toggle(player);
        Assert(Snapshot(feature, player).Enabled == 1 && Snapshot(feature, player).AimY == 2568,
            "F6 immediate start or inverted-gravity aim changed.");
        feature.Toggle(player);
        feature.Arm(player);
        feature.Toggle(player);
        Assert(!feature.Enabled, "F6 did not cancel an armed start.");
    }

    private static void BaitLoss()
    {
        var player = new Player(); var feature = Create(); feature.Arm(player);
        player.BaitPower = 0;
        Before(feature, player);
        Assert(!feature.Enabled, "Bait loss left an armed start active.");
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
