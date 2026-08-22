using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.Shared.Enums;
using ImGuiNET;
using SharpDX;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static System.Enum;
using Vector2 = System.Numerics.Vector2;

namespace AreaStatVisual;

public class AreaStatVisual : BaseSettingsPlugin<AreaStatVisualSettings>
{
    private const RegexOptions RxOpts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly Dictionary<string, Color> Empty = new();
    private static readonly Regex SpacedCamelCase = new("(?<!^)([A-Z])", RegexOptions.Compiled);

    private readonly Dictionary<string, Color> _lineBuffer = new();
    private readonly Dictionary<string, Vector2> _measureCache = new(StringComparer.Ordinal);

    private readonly List<(string t, Color c, Vector2 sz)> _measureScratch = [];
    private readonly List<(string t, Color c, Vector2 pos, Vector2 sz)> _placedScratch = [];

    private readonly Dictionary<GameStat, int> _singleStatValues = new(1);

    private readonly Dictionary<(GameStat Stat, int Value), string?> _statTranslationByKey = new();
    private string _measureCustomFontSpec = "";
    private bool _measureUseCustomFont;
    private string _mapStatFilter = "";

    private List<StatRule>? _rules;

    public AreaStatVisual()
    {
        Name = "Area Stat Visual";
    }

    public override void DrawSettings()
    {
        base.DrawSettings();

        ImGui.Separator();
        if (ImGui.Button("Refresh rules and display")) RefreshDisplay();
        ImGui.SameLine();
        ImGui.TextDisabled("Apply edited regex and display settings immediately.");

        if (!ImGui.CollapsingHeader("Debug: current map stats")) return;

        var mapStats = GameController.InGame ? GameController.IngameState.Data?.MapStats : null;
        if (mapStats == null)
        {
            ImGui.TextDisabled("Enter an area to inspect its map stats.");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##mapStatFilter", "Filter internal stat names...", ref _mapStatFilter, 256);

        var visibleStats = mapStats
            .Where(x => string.IsNullOrWhiteSpace(_mapStatFilter) ||
                        x.Key.ToString().Contains(_mapStatFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Key.ToString(), StringComparer.Ordinal)
            .ToList();

        ImGui.TextDisabled($"Showing {visibleStats.Count} of {mapStats.Count} stat(s). Use the name on the left as GameStatRegex.");
        foreach (var stat in visibleStats)
        {
            ImGui.TextUnformatted($"{stat.Key} = {stat.Value}");
        }
    }

    public override void Render()
    {
        if (!Settings.Enable || !GameController.InGame) return;
        if (!ShouldDraw()) return;

        var lines = BuildLines();
        if (lines.Count == 0) return;

        DrawText(lines);
    }

    public override void AreaChange(AreaInstance _)
    {
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        _rules = null;
        _lineBuffer.Clear();
        _measureCache.Clear();
        _statTranslationByKey.Clear();
    }

    private Dictionary<string, Color> BuildLines()
    {
        var data = GameController.InGame ? GameController.IngameState.Data : null;
        var statSource = data?.MapStats;
        if (statSource == null) return Empty;

        _rules ??= BuildRules();
        _lineBuffer.Clear();
        _statTranslationByKey.Clear();
        FillMatchedLines(GameController.Files, statSource, _rules, _lineBuffer);
        return _lineBuffer;
    }

    private List<StatRule> BuildRules()
    {
        var list = new List<StatRule>();
        foreach (var row in Settings.AreaStats.Content)
        {
            var raw = row.GameStatRegex.Value?.Trim();
            if (string.IsNullOrEmpty(raw)) continue;

            if (TryParseAsGameStat(raw, out var stat))
            {
                list.Add(new StatRule(row, stat, stat.ToString(), null));
                continue;
            }

            if (TryCompilePattern(raw, out var rx)) list.Add(new StatRule(row, null, null, rx));
        }

        return list;
    }

    private static bool TryParseAsGameStat(string trimmed, out GameStat stat)
    {
        if (string.IsNullOrEmpty(trimmed))
        {
            stat = default;
            return false;
        }

        return TryParse(trimmed, true, out stat) && IsDefined(typeof(GameStat), stat);
    }

    private static bool TryCompilePattern(string raw, out Regex? rx)
    {
        rx = null;
        try
        {
            rx = new Regex(raw, RxOpts, TimeSpan.FromMilliseconds(50));
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void FillMatchedLines(FilesContainer? files, IReadOnlyDictionary<GameStat, int> map, List<StatRule> rules, Dictionary<string, Color> dict)
    {
        foreach (var r in rules.Where(r => r.Row.Show.Value))
        {
            if (r.Stat is { } gs)
            {
                if (!map.TryGetValue(gs, out var v)) continue;
                AddLine(dict, files, r.Row, r.StatKey!, v, gs);
                continue;
            }

            var rx = r.Pattern!;
            foreach (var kv in map)
            {
                var keyStr = kv.Key.ToString();
                if (!rx.IsMatch(keyStr)) continue;
                AddLine(dict, files, r.Row, keyStr, kv.Value, kv.Key);
            }
        }
    }

    private void AddLine(Dictionary<string, Color> dict, FilesContainer? files, CustomAreaStatSettings row, string key, int value, GameStat stat)
    {
        string text;
        if (row.UseGameStatTranslation.Value && files != null && TryGetDisplayTranslation(files, stat, value, out var translated)) text = translated;
        else text = !string.IsNullOrEmpty(row.ReplacementString.Value) ? row.ReplacementString.Value : SpacedCamelCase.Replace(key, " $1");

        if (row.ShowKeyValue.Value) text += $": {value}";
        dict[text] = row.TextColor.Value;
    }

    private bool TryGetDisplayTranslation(FilesContainer files, GameStat stat, int value, out string text)
    {
        var key = (stat, value);
        if (_statTranslationByKey.TryGetValue(key, out var cached))
        {
            if (cached != null)
            {
                text = cached;
                return true;
            }

            text = null!;
            return false;
        }

        _singleStatValues.Clear();
        _singleStatValues[stat] = value;
        var raw = ResolveStatDescriptionTranslation(files, _singleStatValues);
        if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('<'))
        {
            _statTranslationByKey[key] = null;
            text = null!;
            return false;
        }

        var normalized = raw.Replace("\r\n", "\n").Replace("\n", " ");
        _statTranslationByKey[key] = normalized;
        text = normalized;
        return true;
    }

    private static string ResolveStatDescriptionTranslation(FilesContainer files, IReadOnlyDictionary<GameStat, int> values)
    {
        var primary = files.StatDescriptions.TranslateMod(values);
        if (!primary.StartsWith('<')) return primary;

        var heist = files.HeistEquipmentStatDescriptions.TranslateMod(values);
        if (!heist.StartsWith('<')) return heist;

        var map = files.MapStatDescriptions.TranslateMod(values);
        return !map.StartsWith('<') ? map : primary;
    }

    private void DrawText(Dictionary<string, Color> lines)
    {
        var ui = Settings.Display;
        var vis = ui.Visuals;
        var rect = GameController.Window.GetWindowRectangle();

        var ax = rect.Width * (ui.Position.XPos.Value / 100f);
        var ay = rect.Height * (ui.Position.YPos.Value / 100f);

        var useCustom = vis.UseCustomFont.Value;
        var customSpec = vis.CustomLoadedFont.Value ?? "";
        if (useCustom != _measureUseCustomFont || customSpec != _measureCustomFontSpec)
        {
            _measureCache.Clear();
            _measureUseCustomFont = useCustom;
            _measureCustomFontSpec = customSpec;
        }

        _measureScratch.Clear();
        foreach (var (t, c) in lines)
        {
            if (!_measureCache.TryGetValue(t, out var sz))
            {
                sz = vis.UseCustomFont ? Graphics.MeasureText(t, vis.CustomLoadedFont.Value) : Graphics.MeasureText(t);
                _measureCache[t] = sz;
            }

            _measureScratch.Add((t, c, sz));
        }

        float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
        _placedScratch.Clear();
        var y = ay;

        foreach (var (t, c, sz) in _measureScratch)
        {
            var x = vis.RightAlignedText.Value ? ax - sz.X : ax;
            Vector2 pos;
            if (!vis.DrawAscending.Value)
            {
                pos = new Vector2(x, y);
                y += sz.Y + vis.TextSpacing.Value;
            }
            else
            {
                y -= sz.Y;
                pos = new Vector2(x, y);
                y -= vis.TextSpacing.Value;
            }

            _placedScratch.Add((t, c, pos, sz));
            xMin = Math.Min(xMin, x);
            xMax = Math.Max(xMax, x + sz.X);
            yMin = Math.Min(yMin, pos.Y);
            yMax = Math.Max(yMax, pos.Y + sz.Y);
        }

        var pad = vis.BorderPadding.Value;
        var box = new RectangleF(xMin - pad, yMin - pad, xMax - xMin + 2 * pad, yMax - yMin + 2 * pad);
        Graphics.DrawBox(box, vis.BackgroundColor.Value, vis.BorderRounding.Value);
        Graphics.DrawFrame(box, vis.BorderColor.Value, vis.BorderRounding.Value, vis.BorderThickness.Value, (int)ImDrawFlags.RoundCornersAll);

        foreach (var (t, c, pos, _) in _placedScratch)
        {
            if (vis.UseCustomFont) Graphics.DrawText(t, pos, c, vis.CustomLoadedFont.Value, FontAlign.Left);
            else Graphics.DrawText(t, pos, c, FontAlign.Left);
        }
    }

    private bool ShouldDraw()
    {
        var ui = GameController?.IngameState.IngameUi;
        if (ui == null) return false;

        var p = Settings.Display.DisplayWithPanels;
        if (!p.RenderOnFullPanels && ui.FullscreenPanels.Any(x => x.IsVisible)) return false;
        if (!p.RenderOnLargePanels && ui.LargePanels.Any(x => x.IsVisible)) return false;
        if (!p.RenderOnLeftPanels && ui.OpenLeftPanel.IsVisible) return false;
        return p.RenderOnRightPanels || !ui.OpenRightPanel.IsVisible;
    }

    private readonly record struct StatRule(CustomAreaStatSettings Row, GameStat? Stat, string? StatKey, Regex? Pattern);
}
