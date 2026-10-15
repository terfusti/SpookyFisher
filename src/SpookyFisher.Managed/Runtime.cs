using System;
using System.Threading;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Terraria;

namespace SpookyFisher
{
    internal sealed class Runtime
    {
        private const string HarmonyId = "terfusti.spookyfisher";
        private static readonly object lifecycle = new object();
        private static Runtime current;
        private readonly Harmony harmony = new Harmony(HarmonyId);
        private readonly CommandQueue<Player> commands = new CommandQueue<Player>(64);
        private ControlEvents controls;
        private CommandSession commandSession;
        private bool stopping, cleanupFailureLogged;
        internal readonly FishingFeature Fishing = new FishingFeature();
        private Overlay overlay;

        private sealed class CommandSession
        {
            internal readonly Player Player;
            internal CommandSession(Player player) { Player = player; }
        }

        internal static Runtime Current => Volatile.Read(ref current);
        internal bool Running => !stopping;
        internal bool CapturesMouse => Running && overlay != null && overlay.CapturesMouse;
        internal bool CapturesKeyboard => Running && overlay != null && overlay.CapturesKeyboard;

        internal static int Start(string eventPrefix)
        {
            lock (lifecycle)
            {
                if (Current != null) return 1;
                Runtime runtime = null;
                try
                {
                    runtime = new Runtime();
                    runtime.controls = new ControlEvents(eventPrefix);
                    runtime.overlay = new Overlay();
                    Log.SetSink(runtime.overlay.Native.WriteLog.Invoke);
                    runtime.Fishing.Initialize();
                    runtime.harmony.PatchAll(typeof(Runtime).Assembly);
                    // Callbacks cannot see a partially initialized runtime.
                    Volatile.Write(ref current, runtime);
                    Log.Write("Hooks installed. Insert: menu; F6: fishing; F7: detach.");
                    return 0;
                }
                catch (Exception ex)
                {
                    Log.Write("Initialization failed: " + ex);
                    try { runtime?.overlay?.Dispose(); }
                    catch (Exception cleanupError) { Log.Write("UI cleanup failed: " + cleanupError); }
                    Log.SetSink(null);
                    try { runtime?.harmony.UnpatchAll(HarmonyId); }
                    catch (Exception cleanupError)
                    {
                        Log.Write("Patch cleanup failed: " + cleanupError);
                        // Retain ownership so a later Start cannot hide a failed cleanup.
                        runtime.stopping = true;
                    }
                    runtime?.commands.Close();
                    runtime?.controls?.Dispose();
                    if (runtime != null)
                    {
                        runtime.controls = null;
                        if (runtime.stopping) Volatile.Write(ref current, runtime);
                    }
                    return 1;
                }
            }
        }

        internal bool TryEnqueue(Action<Player> command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            var session = Volatile.Read(ref commandSession);
            if (session == null) return false;
            return commands.TryEnqueue(player =>
            {
                // A fresh session also protects against reusing the same Player after a menu/pause.
                if (ReferenceEquals(Volatile.Read(ref commandSession), session) && !Main.gamePaused &&
                    ReferenceEquals(player, session.Player) && ReferenceEquals(LocalPlayer(), player))
                    command(player);
            });
        }

        internal void BeforeUpdate()
        {
            if (stopping) return;
            try
            {
                // Detach takes precedence over all queued feature work.
                int uiActions = overlay.Native.TakeActions();
                if (controls.StopRequested || (uiActions & NativeUi.Detach) != 0)
                {
                    BeginStop();
                    return;
                }
                Player player = LocalPlayer();
                // Stop works even during pause; starts are validated again on this update.
                if ((uiActions & NativeUi.StopFishing) != 0) Fishing.Disable("AFK OFF.");
                if (player == null || Main.gamePaused) Volatile.Write(ref commandSession, null);
                else if (commandSession == null || !ReferenceEquals(commandSession.Player, player))
                    Volatile.Write(ref commandSession, new CommandSession(player));
                bool toggle = controls.TakeToggle();
                if ((uiActions & NativeUi.StopFishing) != 0) toggle = false;
                if (toggle)
                {
                    if (player != null) Fishing.Toggle(player);
                    else Log.Write("Enter a world before enabling fishing.");
                }
                else if ((uiActions & NativeUi.StopFishing) == 0 &&
                    (uiActions & NativeUi.StartFishing) != 0 && !Fishing.Enabled && player != null)
                    Fishing.Arm(player);
                if (player == null)
                {
                    commands.Clear();
                    Fishing.Disable(null);
                    return;
                }
                // Paused frames must not accumulate edits that fire later on resume.
                if (Main.gamePaused) commands.Clear();
                else commands.Drain(player, 16, ex => Log.Write("Command failed: " + ex));
            }
            catch (Exception ex)
            {
                Log.Write("Runtime update failed; detaching: " + ex);
                BeginStop();
            }
        }

        private static Player LocalPlayer()
        {
            if (Main.gameMenu || Main.netMode == 2 || Main.myPlayer < 0 ||
                Main.myPlayer >= Main.player.Length) return null;
            Player player = Main.player[Main.myPlayer];
            return player != null && player.active && !player.dead ? player : null;
        }

        internal void Draw(Game game)
        {
            if (!Running) return;
            try
            {
                Player player = LocalPlayer();
                string status, rod;
                var state = Fishing.GetUiState(player, out status, out rod);
                overlay.Native.SetFishing(ref state, status, rod);
                overlay.Draw(game);
            }
            catch (Exception ex)
            {
                Log.Write("Menu rendering failed; detaching: " + ex);
                BeginStop();
            }
        }

        private void BeginStop()
        {
            stopping = true;
            Volatile.Write(ref commandSession, null);
            commands.Close();
            Fishing.Disable(null);
        }

        // The outer update finalizer runs after all temporary ItemCheck input is restored.
        internal void AfterUpdate()
        {
            if (!stopping || controls == null) return;
            lock (lifecycle)
            {
                try
                {
                    Fishing.Disable(null);
                    overlay?.Dispose();
                    harmony.UnpatchAll(HarmonyId);
                    Log.Write("Managed hooks removed. Native bridge may now unload.");
                    Log.SetSink(null);
                    controls.AcknowledgeDetach();
                    controls.Dispose();
                    Volatile.Write(ref current, null);
                }
                catch (Exception ex)
                {
                    // Retain ownership on failure; never acknowledge failed cleanup.
                    if (!cleanupFailureLogged) Log.Write("Detach failed; bridge remains loaded: " + ex);
                    cleanupFailureLogged = true;
                }
            }
        }
    }
}
