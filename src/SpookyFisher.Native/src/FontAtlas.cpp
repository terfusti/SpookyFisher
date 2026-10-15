#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include "FontAtlas.h"
#include "imgui.h"
#include <algorithm>
#include <cmath>
#include <iterator>
#include <stdexcept>
#include <vector>

#pragma comment(lib, "gdi32.lib")

namespace {
    struct FontContext {
        HDC dc = CreateCompatibleDC(nullptr);
        HFONT font = nullptr;
        HGDIOBJ previous = nullptr;

        ~FontContext() {
            if (previous && previous != HGDI_ERROR) SelectObject(dc, previous);
            if (font) DeleteObject(font);
            if (dc) DeleteDC(dc);
        }
    };

    struct GlyphBitmap {
        int rectangle, width, height, stride;
        std::vector<unsigned char> coverage;
    };
}

bool BuildHintedFontAtlas(ImFontAtlas& atlas, float pixelSize) {
    FontContext drawing;
    if (!drawing.dc) return false;
    drawing.font = CreateFontW(-static_cast<int>(std::round(pixelSize)), 0, 0, 0, FW_NORMAL,
        FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS,
        ANTIALIASED_QUALITY, DEFAULT_PITCH, L"Segoe UI");
    if (!drawing.font) return false;
    drawing.previous = SelectObject(drawing.dc, drawing.font);
    if (!drawing.previous || drawing.previous == HGDI_ERROR) return false;
    TEXTMETRICW metrics{};
    if (!GetTextMetricsW(drawing.dc, &metrics)) return false;

    ImFontConfig config;
    config.SizePixels = pixelSize;
    config.OversampleH = config.OversampleV = 1;
    config.PixelSnapH = true;
    strcpy_s(config.Name, "Segoe UI / Windows hinted");
    // The default font supplies atlas/cursor setup and a fallback for absent glyphs.
    // Custom rectangles replace its glyphs with Windows' hinted Segoe UI outlines.
    ImFont* font = atlas.AddFontDefault(&config);
    const float baseline = static_cast<float>(metrics.tmAscent - metrics.tmInternalLeading);
    MAT2 transform{};
    transform.eM11.value = transform.eM22.value = 1;
    const unsigned ranges[] = { 0x20, 0xFF, 0x400, 0x52F, 0x2000, 0x206F };
    std::vector<GlyphBitmap> glyphs;
    for (size_t range = 0; range < std::size(ranges); range += 2) {
        for (unsigned codepoint = ranges[range]; codepoint <= ranges[range + 1]; ++codepoint) {
            const wchar_t character = static_cast<wchar_t>(codepoint);
            WORD glyphIndex = 0;
            if (GetGlyphIndicesW(drawing.dc, &character, 1, &glyphIndex, GGI_MARK_NONEXISTING_GLYPHS) == GDI_ERROR ||
                glyphIndex == 0xFFFF) continue;
            GLYPHMETRICS shape{};
            const DWORD bytes = GetGlyphOutlineW(drawing.dc, codepoint, GGO_GRAY8_BITMAP,
                &shape, 0, nullptr, &transform);
            if (bytes == GDI_ERROR) continue;
            GlyphBitmap glyph{};
            glyph.width = static_cast<int>(shape.gmBlackBoxX);
            glyph.height = static_cast<int>(shape.gmBlackBoxY);
            glyph.stride = (glyph.width + 3) & ~3;
            glyph.coverage.resize(bytes);
            if (bytes && GetGlyphOutlineW(drawing.dc, codepoint, GGO_GRAY8_BITMAP, &shape,
                bytes, glyph.coverage.data(), &transform) == GDI_ERROR) continue;
            glyph.rectangle = atlas.AddCustomRectFontGlyph(font, static_cast<ImWchar>(codepoint),
                std::max(1, glyph.width), std::max(1, glyph.height), static_cast<float>(shape.gmCellIncX),
                ImVec2(static_cast<float>(shape.gmptGlyphOrigin.x), baseline - shape.gmptGlyphOrigin.y));
            glyphs.push_back(std::move(glyph));
        }
    }
    if (!atlas.Build()) throw std::runtime_error("Hinted font atlas build failed");
    unsigned char* pixels = nullptr;
    int width, height;
    atlas.GetTexDataAsAlpha8(&pixels, &width, &height);
    for (const auto& glyph : glyphs) {
        // GDI can report nonzero metrics but an empty bitmap for spacing glyphs.
        if (glyph.coverage.size() < static_cast<size_t>(glyph.stride) * glyph.height) continue;
        const auto* rectangle = atlas.GetCustomRectByIndex(glyph.rectangle);
        if (!rectangle->IsPacked() || rectangle->X + glyph.width > width || rectangle->Y + glyph.height > height)
            throw std::runtime_error("Hinted glyph lies outside the font atlas");
        for (int y = 0; y < glyph.height; ++y)
            for (int x = 0; x < glyph.width; ++x) {
                const unsigned coverage = glyph.coverage[y * glyph.stride + x];
                pixels[(rectangle->Y + y) * width + rectangle->X + x] =
                    static_cast<unsigned char>(std::min(255u, (coverage * 255u + 32u) / 64u));
            }
    }
    // The renderer requests RGBA later; discard any conversion made before our glyph upload.
    if (atlas.TexPixelsRGBA32) { IM_FREE(atlas.TexPixelsRGBA32); atlas.TexPixelsRGBA32 = nullptr; }
    font->Ascent = baseline;
    font->Descent = -static_cast<float>(metrics.tmDescent);
    return true;
}
