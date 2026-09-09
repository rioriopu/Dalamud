using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using CheapLoc;

using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Configuration.Internal;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Internal;
using Dalamud.Utility;
using Dalamud.Utility.Internal;

namespace Dalamud.Interface.Internal.Windows.Settings.Widgets;

[SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Internals")]
internal sealed class DevPluginsSettingsEntry : SettingsEntry
{
    private readonly FileDialogManager fileDialogManager = new();
    private readonly Func<bool>? checkVisibility;

    private List<DevPluginLocationSettings> devPluginLocations = [];
    private bool devPluginLocationsChanged;
    private string devPluginTempLocation = string.Empty;
    private string devPluginLocationAddError = string.Empty;
    private bool hadDevPlugins;

    // [estell] TryGetError の結果をここに持つ。
    //
    // 本家(3a9ec4b3c で追加)は Draw() から直接 TryGetError を呼んでおり、
    // その中で File.Exists が最大2回走る。ImGui の Draw は表示中ずっと毎フレーム
    // 呼ばれるため、登録件数ぶんのファイルI/Oが毎フレーム発生していた。
    // 到達できないネットワーク共有では SMB のタイムアウト待ちで1回 300ms を超え、
    // Experimental タブを開いている間だけゲームが極端に重くなる。
    //
    // 存在確認の結果は毎フレーム変わるものではないので、間隔を置いて評価する。
    private readonly Dictionary<string, (bool HasError, LocationError Error)> errorCache = new();
    private readonly Stopwatch errorCacheAge = new();

    public DevPluginsSettingsEntry(Func<bool>? visibility = null)
    {
        this.Name = LazyLoc.Localize("DalamudSettingsDevPluginLocation", "Dev Plugin Locations");
        this.checkVisibility = visibility;
    }

    public override bool IsVisible => this.IsDevModeEnabled || this.hadDevPlugins;

    private bool IsDevModeEnabled => this.checkVisibility?.Invoke() ?? true;

    public override void OnClose()
    {
        this.devPluginLocations =
            [.. Service<DalamudConfiguration>.Get().DevPluginLoadLocations.Select(x => x.Clone())];
    }

    public override void Load()
    {
        this.devPluginLocations =
            [.. Service<DalamudConfiguration>.Get().DevPluginLoadLocations.Select(x => x.Clone())];
        this.devPluginLocationsChanged = false;
        this.InvalidateErrorCache();   // [estell] 開き直したら存在確認をやり直す
        if (this.devPluginLocations.Count > 0)
            this.hadDevPlugins = true;
    }

    public override void Save()
    {
        var config = Service<DalamudConfiguration>.Get();

        config.DevPluginLoadLocations.RemoveAll(existing =>
                                                    this.devPluginLocations.All(loc => loc.Path != existing.Path));

        foreach (var pendingLocation in this.devPluginLocations)
        {
            var existing = config.DevPluginLoadLocations
                                 .FirstOrDefault(x => x.Path == pendingLocation.Path);

            if (existing != null)
            {
                existing.IsEnabled = pendingLocation.IsEnabled;
                existing.Nickname = pendingLocation.Nickname;
            }
            else
            {
                config.DevPluginLoadLocations.Add(pendingLocation);
            }
        }

        if (this.devPluginLocationsChanged)
        {
            _ = Service<PluginManager>.Get().ScanDevPluginsAsync();
            this.devPluginLocationsChanged = false;
        }
    }

    public override void Draw()
    {
        using var id = ImRaii.PushId("devPluginLocation"u8);

        if (!this.IsDevModeEnabled)
        {
            ImGui.TextColoredWrapped(
                ImGuiColors.AttentionForeground,
                LazyLoc.Localize(
                    "DalamudSettingDevModeDisabledWithDevPlugins",
                    "Developer Mode is disabled, but dev plugins were loaded during this session.\n" +
                    "They will stay loaded, but will not show in the installer next time the game is restarted."));
            return;
        }

        ImGui.Text(this.Name);

        if (this.devPluginLocationsChanged)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen))
            {
                ImGui.SameLine();
                ImGui.Text(Loc.Localize("DalamudSettingsChanged", "(Changed)"));
            }
        }

        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey, Loc.Localize("DalamudSettingsDevPluginLocationsHint", "Add dev plugin load locations.\nThis must be a path to the plugin DLL."));

        var locationSelect = Loc.Localize("DalamudDevPluginLocationSelect", "Select Dev Plugin DLL");
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Folder, locationSelect))
        {
            this.fileDialogManager.OpenFileDialog(
                locationSelect,
                ".dll",
                (result, path) =>
                {
                    if (result)
                    {
                        this.devPluginTempLocation = path;
                        this.AddDevPlugin();
                    }
                });
        }

        ImGuiHelpers.ScaledDummy(5);

        ImGui.Columns(5);
        ImGui.SetColumnWidth(0, 18 + (5 * ImGuiHelpers.GlobalScale));
        ImGui.SetColumnWidth(1, ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X - (18 + 16 + 14 + 14) - ((5 + 45 + 26 + 120) * ImGuiHelpers.GlobalScale));
        ImGui.SetColumnWidth(2, 16 + (120 * ImGuiHelpers.GlobalScale));
        ImGui.SetColumnWidth(3, 16 + (45 * ImGuiHelpers.GlobalScale));
        ImGui.SetColumnWidth(4, 14 + (26 * ImGuiHelpers.GlobalScale));

        ImGui.Separator();

        ImGui.Text("#"u8);
        ImGui.NextColumn();
        ImGui.Text("Path"u8);
        ImGui.NextColumn();
        ImGui.Text("Nickname"u8);
        ImGui.NextColumn();
        ImGui.Text("Enabled"u8);
        ImGui.NextColumn();
        ImGui.Text(string.Empty);
        ImGui.NextColumn();

        ImGui.Separator();

        DevPluginLocationSettings locationToRemove = null;

        var locNumber = 1;
        foreach (var devPluginLocationSetting in this.devPluginLocations)
        {
            var isEnabled = devPluginLocationSetting.IsEnabled;

            id.Push(devPluginLocationSetting.Path);

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetColumnWidth() / 2) - 8 - (ImGui.CalcTextSize(locNumber.ToString()).X / 2));
            ImGui.Text(locNumber.ToString());
            ImGui.NextColumn();

            var dllPath = devPluginLocationSetting.Path;
            // [estell] 毎フレーム走るのでキャッシュ経由にする。実体は同じ判定。
            var hasError = this.TryGetErrorCached(dllPath, out var error);

            ImGui.SetNextItemWidth(!hasError ? -1 : ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
            if (ImGui.InputText($"##devPluginLocationInput", ref dllPath, 65535, ImGuiInputTextFlags.EnterReturnsTrue) && devPluginLocationSetting.Path != dllPath)
            {
                // ここは Enter で確定したときだけ通る。毎フレームではないので直接呼ぶ。
                if (!this.TryGetError(dllPath, out error))
                {
                    devPluginLocationSetting.Path = dllPath;
                    this.devPluginLocationsChanged = true;
                    this.InvalidateErrorCache();   // [estell] パスが変わったので次フレームで再評価
                }
                else if (error.Loc.Key is "DalamudDevPluginLocationExists" or "DalamudDevPluginInvalid")
                {
                    this.devPluginLocationAddError = error.Loc.ToString();
                    Task.Delay(5000).ContinueWith(t => this.devPluginLocationAddError = string.Empty);
                }
            }

            if (hasError)
            {
                ImGui.SameLine();
                ImGuiComponents.HelpMarker(
                    error.Loc.ToString(),
                    error.Icon,
                    error.Color ?? Colors.ImGuiColors.WarningForeground);
            }

            ImGui.NextColumn();

            ImGui.SetNextItemWidth(-1);
            var nickname = devPluginLocationSetting.Nickname ?? string.Empty;
            if (ImGui.InputTextWithHint("##devPluginNickname", "Optional...", ref nickname, 64))
            {
                devPluginLocationSetting.Nickname = string.IsNullOrEmpty(nickname) ? null : nickname;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.Localize("DalamudDevPluginNicknameHint", "Optional nickname shown next to the plugin name in the plugin list."));

            ImGui.NextColumn();

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetColumnWidth() / 2) - 7 - (12 * ImGuiHelpers.GlobalScale));
            ImGui.Checkbox("##devPluginLocationCheck"u8, ref isEnabled);
            ImGui.NextColumn();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
            {
                locationToRemove = devPluginLocationSetting;
            }

            id.Pop();

            ImGui.NextColumn();
            ImGui.Separator();

            devPluginLocationSetting.IsEnabled = isEnabled;

            locNumber++;
        }

        if (locationToRemove != null)
        {
            this.devPluginLocations.Remove(locationToRemove);
            this.InvalidateErrorCache();   // [estell] 件数が変わったので作り直す
            this.devPluginLocationsChanged = true;
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetColumnWidth() / 2) - 8 - (ImGui.CalcTextSize(locNumber.ToString()).X / 2));
        ImGui.Text(locNumber.ToString());
        ImGui.NextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##devPluginLocationInput"u8, ref this.devPluginTempLocation, 300);
        ImGui.NextColumn();
        // Nickname
        ImGui.NextColumn();
        // Enabled button
        ImGui.NextColumn();
        if (!string.IsNullOrEmpty(this.devPluginTempLocation) && ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
        {
            this.AddDevPlugin();
        }

        ImGui.Columns(1);

        if (!string.IsNullOrEmpty(this.devPluginLocationAddError))
        {
            ImGui.TextColoredWrapped(new Vector4(1, 0, 0, 1), this.devPluginLocationAddError);
        }
    }

    public override void PostDraw()
    {
        this.fileDialogManager.Draw();
    }

    private static bool ValidDevPluginPath(string path)
        => Path.IsPathRooted(path) && Path.GetExtension(path) == ".dll";

    private void AddDevPlugin()
    {
        this.devPluginTempLocation = this.devPluginTempLocation.Trim('"');
        if (this.devPluginLocations.Any(
                r => string.Equals(r.Path, this.devPluginTempLocation, StringComparison.InvariantCultureIgnoreCase)))
        {
            this.devPluginLocationAddError = Loc.Localize("DalamudDevPluginLocationExists", "Location already exists.");
            Task.Delay(5000).ContinueWith(t => this.devPluginLocationAddError = string.Empty);
        }
        else if (!ValidDevPluginPath(this.devPluginTempLocation))
        {
            this.devPluginLocationAddError = Loc.Localize(
                "DalamudDevPluginInvalid",
                "The entered value is not a valid path to a potential Dev Plugin.\nDid you mean to enter it as a custom plugin repository in the fields below instead?");
            Task.Delay(5000).ContinueWith(t => this.devPluginLocationAddError = string.Empty);
            return;
        }
        else
        {
            this.InvalidateErrorCache();   // [estell] 件数が変わるので作り直す
            this.devPluginLocations.Add(
                new DevPluginLocationSettings
                {
                    Path = this.devPluginTempLocation,
                    IsEnabled = true,
                });
            this.devPluginLocationsChanged = true;
            this.hadDevPlugins = true;
            this.devPluginTempLocation = string.Empty;
        }

        // Enable ImGui asserts if a dev plugin is added, if no choice was made prior
        Service<DalamudConfiguration>.Get().ImGuiAssertsEnabledAtStartup ??= true;
    }

    /// <summary>
    ///     [estell] キャッシュ経由で <see cref="TryGetError"/> の結果を返す。
    ///     毎フレーム呼ばれる Draw から使うのはこちら。
    /// </summary>
    /// <param name="dllPath">確認するパス。</param>
    /// <param name="error">エラー内容。</param>
    /// <returns>エラーがある場合 true。</returns>
    private bool TryGetErrorCached(string dllPath, out LocationError error)
    {
        // 一定時間ごとに捨てて作り直す。ファイルを置き直したときに
        // 警告表示が更新されないと、直したのに直らないように見えてしまう。
        if (!this.errorCacheAge.IsRunning || this.errorCacheAge.ElapsedMilliseconds > 1000)
        {
            this.errorCache.Clear();
            this.errorCacheAge.Restart();
        }

        if (!this.errorCache.TryGetValue(dllPath, out var cached))
        {
            cached = (this.TryGetError(dllPath, out var freshError), freshError);
            this.errorCache[dllPath] = cached;
        }

        error = cached.Error;
        return cached.HasError;
    }

    /// <summary>[estell] 次の Draw で存在確認をやり直させる。</summary>
    private void InvalidateErrorCache()
    {
        this.errorCache.Clear();
        this.errorCacheAge.Reset();
    }

    private bool TryGetError(string dllPath, out LocationError error)
    {
        var dllExists = !dllPath.IsNullOrWhitespace() && File.Exists(dllPath);
        if (!dllExists)
        {
            error = new(LazyLoc.Localize("DalamudDevPluginLocationFileDoesNotExist", "File does not exist."));
            return true;
        }

        if (!ValidDevPluginPath(dllPath))
        {
            error = new(LazyLoc.Localize("DalamudDevPluginInvalid", "The entered value is not a valid path to a potential Dev Plugin.\nDid you mean to enter it as a custom plugin repository in the fields below instead?"));
            return true;
        }

        var manifestPath = Path.ChangeExtension(dllPath, ".json");
        var manifestExists = !manifestPath.IsNullOrWhitespace() && File.Exists(manifestPath);

        if (!manifestExists)
        {
            error = new(LazyLoc.Localize("DalamudDevPluginLocationManifestDoesNotExist", "Manifest does not exist."));
            return true;
        }

        if (this.devPluginLocations.Count(loc => loc.Path == dllPath) > 1)
        {
            error = new(LazyLoc.Localize("DalamudDevPluginLocationExists", "Location already exists."));
            return true;
        }

        error = default;
        return false;
    }

    private record struct LocationError(LazyLoc Loc, FontAwesomeIcon Icon = FontAwesomeIcon.ExclamationTriangle, Vector4? Color = null);
}
