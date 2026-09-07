using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Internal.ReShadeHandling;
using Dalamud.Interface.Style;
using Dalamud.Interface.Windowing.Persistence;
using Dalamud.IoC.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Internal.AutoUpdate;
using Dalamud.Plugin.Internal.Profiles;
using Dalamud.Storage;
using Dalamud.Utility;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

using Serilog.Events;

using Windows.Win32.UI.WindowsAndMessaging;

namespace Dalamud.Configuration.Internal;

/// <summary>
/// Class containing Dalamud settings.
/// </summary>
[Serializable]
[ServiceManager.ProvidedService]
#pragma warning disable SA1015
[InherentDependency<ReliableFileStorage>] // We must still have this when unloading
#pragma warning restore SA1015
internal sealed class DalamudConfiguration : IInternalDisposableService
{
    private static readonly ModuleLog Log = ModuleLog.Create<DalamudConfiguration>();

    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        TypeNameHandling = TypeNameHandling.All,
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        Formatting = Formatting.Indented,
    };

    [JsonIgnore]
    private string? configPath;

    [JsonIgnore]
    private bool isSaveQueued;

    private Task? writeTask;

    /// <summary>
    /// Delegate for the <see cref="DalamudConfiguration.DalamudConfigurationSaved"/> event that occurs when the dalamud configuration is saved.
    /// </summary>
    /// <param name="dalamudConfiguration">The current dalamud configuration.</param>
    public delegate void DalamudConfigurationSavedDelegate(DalamudConfiguration dalamudConfiguration);

    /// <summary>
    /// Event that occurs when dalamud configuration is saved.
    /// </summary>
    public event DalamudConfigurationSavedDelegate? DalamudConfigurationSaved;

    /// <summary>
    /// Gets or sets a list of muted words.
    /// </summary>
    public List<string>? BadWords { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar should flash once a duty is found.
    /// </summary>
    public bool DutyFinderTaskbarFlash { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a message should be sent in chat once a duty is found.
    /// </summary>
    public bool DutyFinderChatMessage { get; set; } = true;

    /// <summary>
    /// Gets or sets the language code to load Dalamud localization with.
    /// </summary>
    public string? LanguageOverride { get; set; } = null;

    /// <summary>
    /// Gets or sets the last loaded Dalamud version.
    /// </summary>
    public string? LastVersion { get; set; } = null;

    /// <summary>
    /// Gets or sets a dictionary of seen FTUE levels.
    /// </summary>
    public Dictionary<string, int> SeenFtueLevels { get; set; } = [];

    /// <summary>
    /// Gets or sets the last loaded Dalamud version.
    /// </summary>
    public string? LastChangelogMajorMinor { get; set; } = null;

    /// <summary>
    /// Gets or sets the chat type used by default for plugin messages.
    /// </summary>
    public XivChatType GeneralChatType { get; set; } = XivChatType.Debug;

    /// <summary>
    /// Gets or sets a value indicating whether plugin testing builds should be shown.
    /// </summary>
    public bool DoPluginTest { get; set; } = false;

    /// <summary>
    /// Gets or sets a list of custom repos.
    /// </summary>
    public List<ThirdPartyRepoSettings> ThirdRepoList { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether a disclaimer regarding third-party repos has been dismissed.
    /// </summary>
    public bool? ThirdRepoSpeedbumpDismissed { get; set; } = null;

    /// <summary>
    /// Gets or sets a list of hidden plugins.
    /// </summary>
    public List<string> HiddenPluginInternalName { get; set; } = [];

    /// <summary>
    /// Gets or sets a list of favorite plugins.
    /// </summary>
    public List<string> FavoritePluginInternalName { get; set; } = [];

    /// <summary>
    /// Gets or sets a list of pinned plugins.
    /// </summary>
    public List<string> PinnedPluginInternalName { get; set; } = [];

    /// <summary>
    /// Gets or sets a list of seen plugins.
    /// </summary>
    public List<string> SeenPluginInternalName { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether developer mode is enabled.
    /// </summary>
    public bool? DevMode { get; set; }

    /// <summary>
    /// Gets or sets a list of additional settings for devPlugins. The key is the absolute path
    /// to the plugin DLL. This is automatically generated for any plugins in the devPlugins folder.
    /// However by specifiying this value manually, you can add arbitrary files outside the normal
    /// file paths.
    /// </summary>
    public Dictionary<string, DevPluginSettings> DevPluginSettings { get; set; } = [];

    /// <summary>
    /// Gets or sets a list of additional locations that dev plugins should be loaded from. This can
    /// be either a DLL or folder, but should be the absolute path, or a path relative to the currently
    /// injected Dalamud instance.
    /// </summary>
    public List<DevPluginLocationSettings> DevPluginLoadLocations { get; set; } = [];

    /// <summary>
    /// Gets or sets the global UI scale.
    /// </summary>
    public float GlobalUiScale { get; set; } = 1.0f;

    /// <summary>
    /// Gets or sets the default font spec.
    /// </summary>
    public IFontSpec? DefaultFontSpec { get; set; }

    /// <summary>Gets or sets the opacity of the IME state indicator.</summary>
    /// <value>0 will hide the state indicator. 1 will make the state indicator fully visible. Values outside the
    /// range will be clamped to [0, 1].</value>
    /// <remarks>See <see cref="SeIconChar.ImeHiragana"/> to <see cref="SeIconChar.ImeChineseLatin"/>.</remarks>
    public float ImeStateIndicatorOpacity { get; set; } = 1f;

    /// <summary>
    /// Gets or sets a value indicating whether plugin UI should be hidden.
    /// </summary>
    public bool ToggleUiHide { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether plugin UI should be hidden during cutscenes.
    /// </summary>
    public bool ToggleUiHideDuringCutscenes { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether plugin UI should be hidden during GPose.
    /// </summary>
    public bool ToggleUiHideDuringGpose { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a message containing Dalamud's current version and the number of loaded plugins should be sent at login.
    /// </summary>
    public bool PrintDalamudWelcomeMsg { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a message containing detailed plugin information should be sent at login.
    /// </summary>
    public bool PrintPluginsWelcomeMsg { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether plugins should be auto-updated.
    /// </summary>
    [Obsolete("Use AutoUpdateBehavior instead.")]
    public bool AutoUpdatePlugins { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Dalamud should add buttons to the system menu.
    /// </summary>
    public bool DoButtonsSystemMenu { get; set; } = true;

    /// <summary>
    /// Gets or sets the default Dalamud debug log level on startup.
    /// </summary>
    public LogEventLevel LogLevel { get; set; } = LogEventLevel.Information;

    /// <summary>
    /// Gets or sets a value indicating whether to write to log files synchronously.
    /// </summary>
    public bool LogSynchronously { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the debug log should scroll automatically.
    /// </summary>
    public bool LogAutoScroll { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the debug log should open at startup.
    /// </summary>
    public bool LogOpenAtStartup { get; set; }

    /// <summary>
    /// Gets or sets the number of lines to keep for the Dalamud Console window.
    /// </summary>
    public int LogLinesLimit { get; set; } = 10000;

    /// <summary>
    /// Gets or sets a list representing the command history for the Dalamud Console.
    /// </summary>
    public List<string> LogCommandHistory { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the dev bar should open at startup.
    /// </summary>
    public bool DevBarOpenAtStartup { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether ImGui asserts should be enabled at startup.
    /// </summary>
    public bool? ImGuiAssertsEnabledAtStartup { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether docking should be globally enabled in ImGui.
    /// </summary>
    public bool IsDocking { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether plugin user interfaces should trigger sound effects.
    /// This setting is effected by the in-game "System Sounds" option and volume.
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "ABI")]
    public bool EnablePluginUISoundEffects { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an additional button allowing pinning and clickthrough options should be shown
    /// on plugin title bars when using the Window System.
    /// </summary>
    public bool EnablePluginUiAdditionalOptions { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether viewports should always be disabled.
    /// </summary>
    public bool IsDisableViewport { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether navigation via a gamepad should be globally enabled in ImGui.
    /// </summary>
    public bool IsGamepadNavigationEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether focus management is enabled.
    /// </summary>
    public bool IsFocusManagementEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to resume game main thread after plugins load.
    /// </summary>
    public bool IsResumeGameAfterPluginLoad { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether any plugin should be loaded when the game is started.
    /// It is reset immediately when read.
    /// </summary>
    public bool PluginSafeMode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating the wait time between plugin unload and plugin assembly unload.
    /// Uses default value that may change between versions if set to null.
    /// </summary>
    public int? PluginWaitBeforeFree { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether crashes during shutdown should be reported.
    /// </summary>
    public bool ReportShutdownCrashes { get; set; }

    /// <summary>
    /// Gets or sets a list of saved styles.
    /// </summary>
    [JsonProperty("SavedStyles")]
    public List<StyleModelV1>? SavedStylesOld { get; set; }

    /// <summary>
    /// Gets or sets a list of saved styles.
    /// </summary>
    [JsonProperty("SavedStylesVersioned")]
    public List<StyleModel>? SavedStyles { get; set; }

    /// <summary>
    /// Gets or sets the name of the currently chosen style.
    /// </summary>
    public string ChosenStyle { get; set; } = "Dalamud Standard";

    /// <summary>
    /// Gets or sets per-character style assignments.
    /// </summary>
    public List<CharacterStyleAssignment> CharacterStyleAssignments { get; set; } = [];

    /// <summary>
    /// Gets or sets a list of saved plugin profiles.
    /// </summary>
    public List<ProfileModel>? SavedProfiles { get; set; }

    /// <summary>
    /// Gets or sets the default plugin profile.
    /// </summary>
    public ProfileModel? DefaultProfile { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether profiles are enabled.
    /// </summary>
    public bool ProfilesEnabled { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the user has seen the profiles tutorial.
    /// </summary>
    public bool ProfilesHasSeenTutorial { get; set; } = false;

    /// <summary>
    /// Gets or sets the default UI preset.
    /// </summary>
    public PresetModel DefaultUiPreset { get; set; } = new();

    /// <summary>
    /// Gets or sets the order of DTR elements, by title.
    /// </summary>
    public List<string>? DtrOrder { get; set; }

    /// <summary>
    /// Gets or sets the list of ignored DTR elements, by title.
    /// </summary>
    public List<string>? DtrIgnore { get; set; }

    /// <summary>
    /// Gets or sets the spacing used for DTR entries.
    /// </summary>
    public int DtrSpacing { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether to swap the
    /// direction in which elements are drawn in the DTR.
    /// False indicates that elements will be drawn from the end of
    /// the left side of the Server Info bar, and continue leftwards.
    /// True indicates the opposite.
    /// </summary>
    public bool DtrSwapDirection { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the title screen menu is shown.
    /// </summary>
    public bool ShowTsm { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to reduce motions (animations).
    /// </summary>
    public bool? ReduceMotions { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether market board data should be uploaded.
    /// </summary>
    public bool IsMbCollect { get; set; } = true;

    /// <summary>
    /// Gets the ISO 639-1 two-letter code for the language of the effective Dalamud display language.
    /// </summary>
    public string EffectiveLanguage
    {
        get
        {
            var languages = Localization.ApplicableLangCodes.Prepend("en").ToArray();
            try
            {
                if (string.IsNullOrEmpty(this.LanguageOverride))
                {
                    var currentUiLang = CultureInfo.CurrentUICulture;

                    if (Localization.ApplicableLangCodes.Any(x => currentUiLang.TwoLetterISOLanguageName == x))
                        return currentUiLang.TwoLetterISOLanguageName;
                    else
                        return languages[0];
                }
                else
                {
                    return this.LanguageOverride;
                }
            }
            catch (Exception)
            {
                return languages[0];
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether to show info on dev bar.
    /// </summary>
    public bool ShowDevBarInfo { get; set; } = true;

    /// <summary>
    /// Gets or sets the last-used contact details for the plugin feedback form.
    /// </summary>
    public string LastFeedbackContactDetails { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a list of plugins that testing builds should be downloaded for.
    /// </summary>
    public List<PluginTestingOptIn> PluginTestingOptIns { get; set; } = [];

    /// <summary>
    /// Gets or sets a list of plugins that have opted into or out of auto-updating.
    /// </summary>
    public List<AutoUpdatePreference> PluginAutoUpdatePreferences { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the FFXIV window should be toggled to immersive mode.
    /// </summary>
    public bool WindowIsImmersive { get; set; } = false;

    /// <summary>Gets or sets the mode specifying how to handle ReShade.</summary>
    [JsonProperty("ReShadeHandlingModeV2")]
    public ReShadeHandlingMode ReShadeHandlingMode { get; set; } = ReShadeHandlingMode.Default;

    /// <summary>Gets or sets the swap chain hook mode.</summary>
    public SwapChainHelper.HookMode SwapChainHookMode { get; set; } = SwapChainHelper.HookMode.ByteCode;

    /// <summary>
    /// Gets or sets hitch threshold for game network up in milliseconds.
    /// </summary>
    public double GameNetworkUpHitch { get; set; } = 30;

    /// <summary>
    /// Gets or sets hitch threshold for game network down in milliseconds.
    /// </summary>
    public double GameNetworkDownHitch { get; set; } = 30;

    /// <summary>
    /// Gets or sets hitch threshold for framework update in milliseconds.
    /// </summary>
    public double FrameworkUpdateHitch { get; set; } = 50;

    /// <summary>
    /// Gets or sets hitch threshold for ui builder in milliseconds.
    /// </summary>
    public double UiBuilderHitch { get; set; } = 100;

    /// <summary>Gets or sets a value indicating whether to track texture allocation by plugins.</summary>
    public bool UseTexturePluginTracking { get; set; }

    /// <summary>
    /// Gets or sets the page of the plugin installer that is shown by default when opened.
    /// </summary>
    public PluginInstallerOpenKind PluginInstallerOpen { get; set; } = PluginInstallerOpenKind.AllPlugins;

    /// <summary>
    /// Gets or sets a value indicating how auto-updating should behave.
    /// </summary>
    public AutoUpdateBehavior? AutoUpdateBehavior { get; set; } = null;

    /// <summary>
    /// Gets or sets a value indicating whether users should be notified regularly about pending updates.
    /// </summary>
    public bool CheckPeriodicallyForUpdates { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether users should be notified about updates in chat.
    /// </summary>
    public bool SendUpdateNotificationToChat { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether disabled plugins should be auto-updated.
    /// </summary>
    public bool UpdateDisabledPlugins { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether disabled plugins should be updated when updating manually.
    /// </summary>
    public bool UpdateDisabledPluginsOnManualUpdate { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating where notifications are anchored to on the screen.
    /// </summary>
    public Vector2 NotificationAnchorPosition { get; set; } = new(1f, 1f);

    /// <summary>
    /// Gets or sets a value indicating whether seasonal events, such as April Fools, should be allowed to run.
    /// </summary>
    public bool AllowSeasonalEvents { get; set; } = true;

#pragma warning disable SA1600
#pragma warning disable SA1516
    // XLCore/XoM compatibility until they move it out
    public string? DalamudBetaKey { get; set; } = null;
    public string? DalamudBetaKind { get; set; }
#pragma warning restore SA1516
#pragma warning restore SA1600

    /// <summary>
    /// Gets or sets a list of badge passwords used to unlock badges.
    /// </summary>
    public List<string> UsedBadgePasswords { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether badges should be shown on the title screen.
    /// </summary>
    public bool ShowBadgesOnTitleScreen { get; set; } = true;

    /// <summary>
    /// Load a configuration from the provided path.
    /// </summary>
    /// <param name="path">Path to read from.</param>
    /// <param name="fs">File storage.</param>
    /// <returns>The deserialized configuration file.</returns>
    public static async Task<DalamudConfiguration> Load(string path, ReliableFileStorage fs)
    {
        DalamudConfiguration deserialized = null;

        // [estell] 実際にファイルから読めた本文。読めたときだけスナップショットを更新する。
        string? loadedText = null;

        // [estell] 型解決に失敗した項目があったか。あった場合は内容が欠けているので
        // スナップショットを上書きしない(欠けた設定を「正常な設定」として残さないため)。
        var degraded = false;

        try
        {
            await fs.ReadAllTextAsync(path, text =>
            {
                // If this reads as null, the file was empty, that's no good
                deserialized = DeserializeLeniently(text, out var hadUnresolvableType)
                    ?? throw new Exception("Read config was null.");

                degraded = hadUnresolvableType;
                loadedText = text;
            });
        }
        catch (FileNotFoundException)
        {
            // ignored
        }
        catch (Exception e)
        {
            Log.Error(e, "Could not load configuration at {Path}, creating new", path);

            // [estell] 本家はここで即座に新規設定を作るため、直後の保存で
            // 読めなかった設定が上書きされ、利用者は復旧手段を完全に失う。
            // 上書きされる前に現物を退避し、直近の正常な設定からの復旧を試みる。
            PreserveBrokenConfig(path);
            deserialized = TryRestoreSnapshot(path);
        }

        if (loadedText is not null && !degraded)
            UpdateSnapshot(path, loadedText);

        deserialized ??= new DalamudConfiguration();
        deserialized.configPath = path;

        try
        {
            deserialized.SetDefaults();
            deserialized.Cleanup();
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to set defaults or cleanup");
        }

        return deserialized;
    }

    /// <summary>
    /// Save the configuration at the path it was loaded from, at the next frame.
    /// </summary>
    public void QueueSave()
    {
        this.isSaveQueued = true;
    }

    /// <summary>
    /// Immediately save the configuration.
    /// </summary>
    public void ForceSave()
    {
        this.Save();
        this.isSaveQueued = false;
        this.writeTask?.GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        // Make sure that we save, if a save is queued while we are shutting down
        this.Update();

        // Wait for the write task to finish
        this.writeTask?.Wait();
    }

    /// <summary>
    /// Save the file, if needed. Only needs to be done once a frame.
    /// </summary>
    internal void Update()
    {
        if (this.isSaveQueued)
        {
            this.Save();
            this.isSaveQueued = false;
        }
    }

    /// <summary>
    /// [estell] 型解決に失敗した項目だけを既定値に落として設定を読む。
    ///
    /// 本家は保存用と同じ設定で読むため、解決できない $type が 1 つでもあると
    /// 設定ファイル全体が捨てられ、プラグイン構成やプロファイルまで失われる。
    /// プラグインが定義した型は本体より後に、しかも独立した AssemblyLoadContext に
    /// 読み込まれるため、本体の設定に書き込まれた時点で二度と解決できない。
    /// 実例として DalamudCNAdapter.CnFontId が DefaultFontSpec に永続化され、
    /// 利用者の設定が全損した。
    ///
    /// JSON 自体の破損など「型解決以外」の異常は握りつぶさない。
    /// 中途半端に読めた設定を保存し直すと、かえって被害が広がるため。
    /// </summary>
    /// <param name="text">設定ファイルの本文。</param>
    /// <param name="degraded">型解決に失敗した項目があった場合 true。</param>
    /// <returns>読み込んだ設定。本文が空の場合は null。</returns>
    private static DalamudConfiguration? DeserializeLeniently(string text, out bool degraded)
    {
        var hadUnresolvableType = false;

        var settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.All,
            TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
            Formatting = Formatting.Indented,
            SerializationBinder = new LenientSerializationBinder(),
            Error = (_, args) =>
            {
                if (!IsUnresolvableType(args.ErrorContext.Error))
                    return;

                hadUnresolvableType = true;
                args.ErrorContext.Handled = true;

                Log.Warning(
                    "Could not resolve type at '{Path}' in DalamudConfiguration, falling back to default: {Message}",
                    args.ErrorContext.Path,
                    args.ErrorContext.Error.Message);
            },
        };

        var result = JsonConvert.DeserializeObject<DalamudConfiguration>(text, settings);
        degraded = hadUnresolvableType;
        return result;
    }

    /// <summary>
    /// [estell] 例外の連鎖に型解決の失敗が含まれるか調べる。
    /// </summary>
    /// <param name="ex">調べる例外。</param>
    /// <returns>型解決の失敗が含まれる場合 true。</returns>
    private static bool IsUnresolvableType(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is UnresolvableTypeException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// [estell] 復旧用スナップショットのパスを返す。
    /// </summary>
    /// <param name="path">設定ファイルのパス。</param>
    /// <returns>スナップショットのパス。</returns>
    private static string GetSnapshotPath(string path) => path + ".good";

    /// <summary>
    /// [estell] 正常に読めた設定を控えとして残す。次回の読み込みに失敗したとき、ここから復旧する。
    /// 書き込み途中で中断しても壊れないよう、一時ファイル経由で置き換える。
    /// </summary>
    /// <param name="path">設定ファイルのパス。</param>
    /// <param name="text">正常に読めた本文。</param>
    private static void UpdateSnapshot(string path, string text)
    {
        var snapshot = GetSnapshotPath(path);

        try
        {
            var temp = snapshot + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, snapshot, true);
        }
        catch (Exception ex)
        {
            // 控えが作れなくても起動は妨げない
            Log.Warning(ex, "Failed to update DalamudConfiguration snapshot at {Path}", snapshot);
        }
    }

    /// <summary>
    /// [estell] 読めなかった設定を、新規設定で上書きされる前に退避する。
    /// これが無いと、利用者は原因調査も手動復旧もできなくなる。
    /// </summary>
    /// <param name="path">設定ファイルのパス。</param>
    private static void PreserveBrokenConfig(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var preserved = $"{path}.broken-{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Copy(path, preserved, true);
            Log.Warning("Preserved unreadable DalamudConfiguration as {Path}", preserved);

            PruneBrokenConfigs(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to preserve unreadable DalamudConfiguration at {Path}", path);
        }
    }

    /// <summary>
    /// [estell] 退避した設定が際限なく溜まらないよう、新しいものから 5 世代だけ残す。
    /// タイムスタンプは辞書順が時系列順になる書式なので、名前で並べ替えれば足りる。
    /// </summary>
    /// <param name="path">設定ファイルのパス。</param>
    private static void PruneBrokenConfigs(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return;

        var stale = Directory.GetFiles(directory, Path.GetFileName(path) + ".broken-*")
                             .OrderByDescending(x => x)
                             .Skip(5);

        foreach (var file in stale)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to prune old DalamudConfiguration backup {Path}", file);
            }
        }
    }

    /// <summary>
    /// [estell] 直近の正常な設定から復旧する。
    /// </summary>
    /// <param name="path">設定ファイルのパス。</param>
    /// <returns>復旧できた設定。控えが無い、または読めない場合は null。</returns>
    private static DalamudConfiguration? TryRestoreSnapshot(string path)
    {
        var snapshot = GetSnapshotPath(path);

        try
        {
            if (!File.Exists(snapshot))
            {
                Log.Warning("No DalamudConfiguration snapshot at {Path}, starting fresh", snapshot);
                return null;
            }

            var restored = DeserializeLeniently(File.ReadAllText(snapshot), out _);
            if (restored is null)
            {
                Log.Error("DalamudConfiguration snapshot at {Path} was empty", snapshot);
                return null;
            }

            Log.Information("Restored DalamudConfiguration from snapshot {Path}", snapshot);
            return restored;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restore DalamudConfiguration from snapshot {Path}", snapshot);
            return null;
        }
    }

    private void SetDefaults()
    {
#pragma warning disable CS0618
        // "Reduced motion"
        if (!this.ReduceMotions.HasValue)
        {
            // https://source.chromium.org/chromium/chromium/src/+/main:ui/gfx/animation/animation_win.cc;l=29?q=ReducedMotion&ss=chromium
            var winAnimEnabled = 0;
            bool success;
            unsafe
            {
                success = Windows.Win32.PInvoke.SystemParametersInfo(
                    SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCLIENTAREAANIMATION,
                    0,
                    &winAnimEnabled,
                    0);
            }

            if (!success)
            {
                Log.Warning("Failed to get Windows animation setting, assuming reduced motion is off (GetLastError: {GetLastError:X})", Marshal.GetLastPInvokeError());
                this.ReduceMotions = false;
            }
            else
            {
                this.ReduceMotions = winAnimEnabled == 0;
            }
        }

        // Migrate old auto-update setting to new auto-update behavior
        this.AutoUpdateBehavior ??= this.AutoUpdatePlugins
                                        ? Plugin.Internal.AutoUpdate.AutoUpdateBehavior.UpdateAll
                                        : Plugin.Internal.AutoUpdate.AutoUpdateBehavior.OnlyNotify;

        this.DevMode ??= this.DevPluginLoadLocations.Count != 0 || this.DevBarOpenAtStartup;
#pragma warning restore CS0618
    }

    private void Cleanup()
    {
        // Unsure of the cause, but a null URL repo is possible
        this.ThirdRepoList.RemoveAll(repo => repo.Url.IsNullOrEmpty());
    }

    private void Save()
    {
        ThreadSafety.AssertMainThread();
        if (this.configPath is null)
            throw new InvalidOperationException("configPath is not set.");

        // Wait for previous write to finish
        this.writeTask?.Wait();

        this.writeTask = Task.Run(async () =>
        {
            await Service<ReliableFileStorage>.Get().WriteAllTextAsync(
                                                   this.configPath,
                                                   JsonConvert.SerializeObject(this, SerializerSettings));
            Log.Verbose("Configuration saved");
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Log.Error(
                    t.Exception,
                    "Failed to save configuration to {Path}",
                    this.configPath);
            }
        });

        foreach (var action in Delegate.EnumerateInvocationList(this.DalamudConfigurationSaved))
        {
            try
            {
                action(this);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Exception during raise of {handler}", action.Method);
            }
        }
    }

    /// <summary>
    /// [estell] 型名を解決できなかったことを示す内部例外。
    /// Newtonsoft が投げるその他の JsonSerializationException と区別するために使う。
    /// </summary>
    private sealed class UnresolvableTypeException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UnresolvableTypeException"/> class.
        /// </summary>
        /// <param name="assemblyName">解決できなかったアセンブリ名。</param>
        /// <param name="typeName">解決できなかった型名。</param>
        /// <param name="innerException">Newtonsoft が投げた元の例外。</param>
        public UnresolvableTypeException(string? assemblyName, string typeName, Exception innerException)
            : base($"Could not resolve type '{typeName}, {assemblyName}'.", innerException)
        {
        }
    }

    /// <summary>
    /// [estell] 型名を解決できないときに <see cref="UnresolvableTypeException"/> を投げるバインダ。
    /// これにより「型が解決できないだけ」の失敗と、それ以外の本物の異常を呼び出し側で区別できる。
    /// </summary>
    private sealed class LenientSerializationBinder : DefaultSerializationBinder
    {
        /// <inheritdoc/>
        public override Type BindToType(string? assemblyName, string typeName)
        {
            try
            {
                return base.BindToType(assemblyName, typeName);
            }
            catch (Exception ex)
            {
                throw new UnresolvableTypeException(assemblyName, typeName, ex);
            }
        }
    }
}
