# In-game ImGui menu

Insert opens/closes a resizable menu in Terraria's existing window, including at
the main menu. The orange-and-black Halloween theme uses Segoe UI, rounded charcoal
panels, and warm orange highlights. A slim title bar reads "Spooky Fisher"; there is
no large logo/banner. The menu opens at 940 x 500 logical pixels with small, centered,
rounded Fishing and Console buttons on the left. Fishing
opens by default. Console shows both native bootstrap and managed runtime logs.

## Fishing controls

Fishing shows enabled/paused/casting/retrieval status, the equipped rod, usable bait
power, active bobbers, retrieval requests this session, failed casts, and world aim.
Hold a fishing rod with bait, click Start fishing, then cast once into the water.
The menu shows "Waiting for your cast" and Cancel start until your cast produces a
local bobber. It saves the world aim from that cast's item-use input and takes over
retrieval and recasting. Arming never injects item use or changes the cursor; clicks
on the panel and existing bobbers cannot choose an aim. Failed casts can be retried.
You can reposition before casting; changing rods cancels the pending start.

F6 remains an instant toggle that captures the current aim, and also cancels an
armed start. Stop fishing works while paused. Start is disabled when the player,
rod, bait, or game state is invalid; no prior aim is required. The status explains
the blocker. Movement and rod changes stop active fishing. Retrievals count requests,
not confirmed inventory additions.

Insert toggles the menu; F7 or Detach requests normal game-thread cleanup.

## Rendering and input

Dear ImGui v1.91.9b is compiled into `SpookyFisher.dll`. Its Win32 backend reads
window input; its meshes are drawn by `XnaUiRenderer` through Terraria's XNA
`GraphicsDevice` after the game's draw callback. There is no extra window, graphics
device, swap chain, or native DirectX renderer. This follows ImGui's separation of
[platform and renderer backends](https://github.com/ocornut/imgui/blob/v1.91.9b/docs/BACKENDS.md).

`UI/NativeUi.cs` defines a small C ABI for font pixels and draw buffers. Pointers
remain valid until the next frame or shutdown; all UI calls run on the window/game
thread. The renderer reuses buffers, honors clip rectangles and vertex offsets,
restores graphics bindings/state, and recreates GPU resources after device resets.
The overlay explicitly selects the final backbuffer and its full viewport for
drawing, then restores the previous target/viewport/scissor. A reduced scene target
or viewport left by the game cannot lower the menu's rendering resolution. Hidden
frames do not switch render targets.
The font atlas is rasterized at the backbuffer-to-client pixel density and refreshed
when that density changes. Layout and mouse coordinates remain in client units;
the XNA half-pixel correction stays in framebuffer pixels, keeping scaled text sharp.
`FontAtlas.cpp` rasterizes Segoe UI with Windows' grayscale, hinted TrueType glyph
outlines at the actual framebuffer pixel size. Custom atlas rectangles carry the
glyph images, bearings, and advances. This uses Windows' small-text grid fitting
instead of the previous unhinted stb rasterization. Text remains grayscale to
avoid colored ClearType fringes over the game. The existing stb path remains a
fallback if a Windows font context cannot be created.
For a high-DPI desktop, `tools/Start-Terraria.ps1` starts the game with application
DPI scaling before its window is created. This prevents Windows from enlarging a
low-resolution backbuffer. The menu then scales fonts, spacing, and initial size
to the window's DPI. Changing font resolution alone cannot undo whole-window
bitmap stretching; use this launcher when the game itself is DPI-unaware.
It currently supports the font atlas texture; add a texture registry before using
custom ImGui images.

The thread message hook captures keys before WinForms preprocessing; the window
callback handles focus, cursor, and direct messages. Insert also has a polled
fallback, with shared state preventing double toggles. Input always feeds ImGui;
its capture flags independently decide whether mouse or keyboard input reaches
Terraria. Mouse input passes through outside the panel. Panel clicks, scrolling,
and drags stay with ImGui, including a drag that leaves the panel. Only the title
bar moves the window. Keyboard controls remain available while hovering the panel;
editing the Console filter captures typing instead.

`SF_UiCaptureInput` returns a bitmask (mouse = 1, keyboard = 2), independently of
`SF_UiVisible`. `MenuInputHooks` removes captured mouse bindings before Terraria
merges them with keyboard bindings, including remapped mouse buttons. Coordinates
and wheel values keep updating, preventing stale positions and delayed scrolling.
Temporary input-block flags are restored in the update finalizer. Closing the menu
waits for any held panel-owned mouse buttons to release before passing clicks through.

## Console and cleanup

Native `Log` and managed `Log.Write` append to the same thread-safe history, capped
at 2,048 lines and 4,096 characters per write. Logs collected before the first draw
are retained. Console provides filtering (`word,-exclude`), Clear, Copy, and
auto-scroll; file logs retain full diagnostics.

Detach removes the message/window hooks and disposes graphics resources before unpatching
and acknowledging native unload. If another component still chains through the
window hook, the bridge stays resident and passes input through. Managed log calls
are disconnected under a lock before acknowledgement; resolving exports from the
loaded module avoids a cached P/Invoke reference preventing native unload.

## Adding features

Add navigation items/pages in `Overlay.cpp::DrawMenu`. For inventory or teleport controls,
add typed native-to-managed commands and copied state snapshots. Fishing uses a
36-byte plain-data snapshot and copied UTF-16 status/rod text. Its enabled field is
0 for off, 1 for active fishing, and 2 for waiting for a manual cast. Start, stop, and
detach action bits are consumed once in `Runtime.BeforeUpdate`, with detach taking
priority and starts revalidated against current game state. General feature commands
can use `GameCommands`. Keep Terraria objects and mutations out of native drawing code.

Run the graphics test commands in the README. Manually check Insert in menus and
a world, dragging/resizing, Console filtering/copy/scroll, held inputs on close,
Alt+Tab, fullscreen/resolution changes, and F7 detachment with the menu open.
