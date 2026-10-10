// Invertonator — Settings: settings.json load-or-create + hotkey parsing
//
//   File: settings.json next to the exe (auto-created with defaults).
//   Hotkey format: "Ctrl+Alt+F5" — modifiers Ctrl/Shift/Alt/Win in any
//   order + a key (F1–F12, A–Z, 0–9, NUM0–9, INSERT/DELETE/HOME/END/
//   PGUP/PGDN). Invalid file → defaults written back, so a broken
//   settings file can never brick the app.

using System;
using System.IO;
using System.Text.Json;

namespace Invertonator;

internal sealed class Settings
{
    public HotkeySettings Hotkeys { get; set; } = new();
    public FeatureSettings Features { get; set; } = new();

    public sealed class HotkeySettings
    {
        public string Invert { get; set; } = "Ctrl+Alt+F5";
        public string Mode { get; set; } = "Ctrl+Alt+F6";
        public string Pierce { get; set; } = "Ctrl+Alt+F7";
        public string Quit { get; set; } = "Ctrl+Alt+F8";
        public string Dump { get; set; } = "Ctrl+Alt+F9";
    }

    public sealed class FeatureSettings
    {
        public bool AtMode { get; set; } = false;
        public double DimLevel { get; set; } = 0.4;
    }

    // ---- load or create ----

    private static string SettingsPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(
                    File.ReadAllText(SettingsPath),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    });
                if (loaded != null)
                {
                    Logger.Log("settings: loaded from settings.json");
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"settings: load failed ({ex.Message}) — using defaults");
        }

        var defaults = new Settings();
        defaults.Save();
        return defaults;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            Logger.Log("settings: saved");
        }
        catch (Exception ex)
        {
            Logger.Log($"settings: save failed: {ex.Message}");
        }
    }

    // ---- hotkey parsing: "Ctrl+Alt+F5" → (mods, vk) ----

    public static (uint mods, uint vk)? ParseHotkey(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;

        uint mods = 0;
        uint vk = 0;
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8,
                   MOD_NOREPEAT = 0x4000;

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "shift":             mods |= MOD_SHIFT;   break;
                case "alt":               mods |= MOD_ALT;     break;
                case "win" or "windows":  mods |= MOD_WIN;     break;
                default:
                    vk = ParseKey(part.ToUpperInvariant());
                    if (vk == 0) return null;   // unknown key → invalid
                    break;
            }
        }

        if (vk == 0 || mods == 0) return null;   // need a key AND at least one modifier
        return (mods | MOD_NOREPEAT, vk);
    }

    private static uint ParseKey(string key)
    {
        // F1–F12
        if (key.Length >= 2 && key[0] == 'F' && uint.TryParse(key[1..], out var f) && f is >= 1 and <= 12)
            return 0x70 + (f - 1);          // VK_F1 = 0x70 … VK_F12 = 0x7B
        // A–Z
        if (key.Length == 1 && key[0] >= 'A' && key[0] <= 'Z')
            return key[0];                   // VK_A…VK_Z = 0x41…0x5A
        // 0–9
        if (key.Length == 1 && key[0] >= '0' && key[0] <= '9')
            return key[0];                   // VK_0…VK_9 = 0x30…0x39
        // Named keys
        return key switch
        {
            "INSERT" => 0x2D, "DELETE" => 0x2E, "HOME" => 0x24, "END" => 0x23,
            "PGUP" => 0x21, "PGDN" => 0x22,
            "NUM0" => 0x60, "NUM1" => 0x61, "NUM2" => 0x62, "NUM3" => 0x63,
            "NUM4" => 0x64, "NUM5" => 0x65, "NUM6" => 0x66, "NUM7" => 0x67,
            "NUM8" => 0x68, "NUM9" => 0x69,
            _ => 0,
        };
    }
}