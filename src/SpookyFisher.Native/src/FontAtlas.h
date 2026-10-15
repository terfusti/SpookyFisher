#pragma once

struct ImFontAtlas;

// Rasterizes grayscale, grid-fitted Windows glyphs at framebuffer resolution.
bool BuildHintedFontAtlas(ImFontAtlas& atlas, float pixelSize);
