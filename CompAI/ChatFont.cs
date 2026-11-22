using System;
using System.IO;
using Dalamud.Interface.ManagedFontAtlas;

namespace CompAI;

// Use a locally installed Windows font with the Latin Extended and Greek glyphs
// required by Romanian chat text and item names. The plugin never downloads fonts.
internal sealed class ChatFont : IDisposable
{
    private readonly IFontHandle? handle;
    private readonly string? setupError;

    internal ChatFont()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
        if (!File.Exists(path))
        {
            this.setupError = "Segoe UI is unavailable; using the Dalamud font.";
            return;
        }
        try
        {
            this.handle = Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
                tk.AddFontFromFile(path, new SafeFontConfig
                {
                    SizePx = Plugin.PluginInterface.UiBuilder.FontDefaultSizePx,
                    GlyphRanges = new ushort[]
                    {
                        0x0020, 0x024F, // Basic Latin, Latin-1, and Latin Extended (Romanian)
                        0x0300, 0x03FF, // Combining marks and Greek
                        0x2000, 0x206F, // Smart punctuation and dashes
                        0x20A0, 0x20CF, // Currency symbols
                        0x2190, 0x22FF, // Arrows and mathematical symbols
                        0,
                    },
                })));
        }
        catch (Exception ex)
        {
            this.setupError = $"Chat font setup failed ({ex.GetType().Name}); using the Dalamud font.";
        }
    }

    internal string? Status => this.setupError ?? (this.handle?.LoadException is { } error
        ? $"Chat font build failed ({error.GetType().Name}); using the Dalamud font." : null);

    internal IDisposable Push() => this.handle?.Push() ?? Noop.Instance;

    public void Dispose() => this.handle?.Dispose();

    private sealed class Noop : IDisposable
    {
        internal static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
