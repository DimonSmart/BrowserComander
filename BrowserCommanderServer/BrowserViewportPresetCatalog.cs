using BrowserCommander.Contracts;

namespace BrowserCommanderServer;

public static class BrowserViewportPresetCatalog
{
    // CSS viewport sizes aligned with common Chrome DevTools phone presets.
    private static readonly IReadOnlyList<BrowserViewportPreset> Presets =
    [
        new()
        {
            Name = "iphone-se",
            Title = "iPhone SE",
            Width = 375,
            Height = 667
        },
        new()
        {
            Name = "iphone-12-pro",
            Title = "iPhone 12 Pro",
            Width = 390,
            Height = 844
        },
        new()
        {
            Name = "pixel-7",
            Title = "Pixel 7",
            Width = 412,
            Height = 915
        },
        new()
        {
            Name = "galaxy-s20-ultra",
            Title = "Galaxy S20 Ultra",
            Width = 412,
            Height = 915
        }
    ];

    private static readonly IReadOnlyDictionary<string, BrowserViewportPreset> PresetsByName =
        Presets.ToDictionary(preset => preset.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<BrowserViewportPreset> All => Presets;

    public static bool TryGetByName(string? name, out BrowserViewportPreset preset)
    {
        if (!string.IsNullOrWhiteSpace(name)
            && PresetsByName.TryGetValue(name.Trim(), out var resolvedPreset))
        {
            preset = resolvedPreset;
            return true;
        }

        preset = new BrowserViewportPreset();
        return false;
    }
}
