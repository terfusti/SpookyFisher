# Dear ImGui

Vendored from [ocornut/imgui, tag v1.91.9b](https://github.com/ocornut/imgui/tree/v1.91.9b).
The unmodified core and Win32 platform backend are compiled into `SpookyFisher.dll`.
The MIT license is retained in [LICENSE.txt](LICENSE.txt).

`Overlay.cpp` owns the context, window input, tabs, and log history.
`XnaUiRenderer.cs` renders its draw data using Terraria's XNA `GraphicsDevice`.
No native DirectX renderer, additional swap chain, or secondary UI window is used.

Keep core, headers, and backend on the same version when updating. The bridge
checks the vertex/index ABI at compile time. Run the UI graphics tests after an
update; newer ImGui texture APIs may require corresponding bridge changes.
