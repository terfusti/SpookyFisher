// Minimal deterministic game state for exercising the production fishing state machine.
// Real game/Harmony signature compatibility is checked by SpookyFisher.Hooks.Tests.
namespace Terraria
{
    internal struct Position { internal float X, Y; }
    internal sealed class Item
    {
        internal int type = 10, fishingPole = 20, useAnimation = 20, useTime = 20;
        internal string Name = "Test rod";
        internal bool IsAir = true;
    }
    internal sealed class Projectile
    {
        internal bool active = true, bobber = true;
        internal int owner;
        internal float[] ai = new float[2], localAI = new float[2];
    }
    internal struct FishingConditions { internal int BaitPower; }
    internal sealed class Player
    {
        internal bool active = true, dead, CCed, noItems, pulley, mouseInterface;
        internal bool controlUseItem, controlUseTile, releaseUseItem = true;
        internal int selectedItem, whoAmI, itemAnimation, itemTime, reuseDelay;
        internal float gravDir = 1;
        internal Position position;
        internal Item[] inventory = { new Item() };
        internal int BaitPower = 35;
        internal FishingConditions GetFishingConditions() => new FishingConditions { BaitPower = BaitPower };
        private void ItemCheck_PullFishingBobbers(Item rod) { }
    }
    internal static class Main
    {
        internal static bool gameMenu, gamePaused, playerInventory, blockInput, drawingPlayerChat;
        internal static Item mouseItem;
        internal static Position screenPosition;
        internal static int mouseX, mouseY, screenHeight = 768;
        internal static Projectile[] projectile = new Projectile[0];
    }
}
namespace Terraria.GameInput
{
    internal static class PlayerInput { internal static int MouseX, MouseY; }
}
namespace SpookyFisher
{
    internal static class Log { internal static void Write(string message) { } }
}
