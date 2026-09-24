using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Logging.Internal;
using Dalamud.Networking.Http;
using Dalamud.Plugin.Internal.Types.Manifest;
using Dalamud.Utility;

using Newtonsoft.Json;

namespace Dalamud.Plugin.Internal.Types;

/// <summary>
/// This class represents a single plugin repository.
/// </summary>
internal class PluginRepository
{
    /// <summary>
    /// The URL of the official main repository.
    /// </summary>
    public const string MainRepoUrl = "https://kamori.goats.dev/Plugin/PluginMaster";

    private const int HttpRequestTimeoutSeconds = 20;

    /// <summary>
    ///     取り直すときのタイムアウト。1 回目より短くしてある。
    ///     一度詰まった相手をもう一度 20 秒待っても利用者を待たせるだけで、
    ///     成功するときは 1 秒もかからないため。
    /// </summary>
    private const int HttpRetryTimeoutSeconds = 10;

    /// <summary>取り直すまでの待ち。詰まりが抜けるだけの間を置く。</summary>
    private const int HttpRetryDelayMilliseconds = 500;

    private static readonly ModuleLog Log = ModuleLog.Create<PluginRepository>();
    private readonly HttpClient httpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginRepository"/> class.
    /// </summary>
    /// <param name="happyHttpClient">An instance of <see cref="HappyHttpClient"/>.</param>
    /// <param name="pluginMasterUrl">The plugin master URL.</param>
    /// <param name="isEnabled">Whether the plugin repo is enabled.</param>
    public PluginRepository(HappyHttpClient happyHttpClient, string pluginMasterUrl, bool isEnabled)
    {
        this.httpClient = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = happyHttpClient.SharedHappyEyeballsCallback.ConnectCallback,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
            DefaultRequestHeaders =
            {
                Accept =
                {
                    new MediaTypeWithQualityHeaderValue("application/json"),
                },
                CacheControl = new CacheControlHeaderValue
                {
                    NoCache = true,
                },
                UserAgent =
                {
                    new ProductInfoHeaderValue("Dalamud", Versioning.GetAssemblyVersion()),
                },
            },
        };
        this.PluginMasterUrl = pluginMasterUrl;
        this.IsThirdParty = pluginMasterUrl != MainRepoUrl;
        this.IsEnabled = isEnabled;
    }

    /// <summary>
    /// Gets the pluginmaster.json URL.
    /// </summary>
    public string PluginMasterUrl { get; }

    /// <summary>
    /// Gets a value indicating whether this plugin repository is from a third party.
    /// </summary>
    public bool IsThirdParty { get; }

    /// <summary>
    /// Gets a value indicating whether this repo is enabled.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// Gets the plugin master list of available plugins.
    /// </summary>
    public ReadOnlyCollection<RemotePluginManifest>? PluginMaster { get; private set; }

    /// <summary>
    /// Gets the initialization state of the plugin repository.
    /// </summary>
    public PluginRepositoryState State { get; private set; }

    /// <summary>
    /// Gets a new instance of the <see cref="PluginRepository"/> class for the main repo.
    /// </summary>
    /// <param name="happyHttpClient">An instance of <see cref="HappyHttpClient"/>.</param>
    /// <returns>The new instance of main repository.</returns>
    public static PluginRepository CreateMainRepo(HappyHttpClient happyHttpClient) =>
        new(happyHttpClient, MainRepoUrl, true);

    /// <summary>
    /// Reload the plugin master asynchronously in a task.
    /// </summary>
    /// <returns>The new state.</returns>
    public async Task ReloadAsync()
    {
        this.State = PluginRepositoryState.InProgress;
        this.PluginMaster = new List<RemotePluginManifest>().AsReadOnly();

        try
        {
            Log.Information($"Fetching repo: {this.PluginMasterUrl}");

            var data = await this.FetchPluginMasterWithRetryAsync(this.PluginMasterUrl);
            var pluginMaster = JsonConvert.DeserializeObject<List<RemotePluginManifest>>(data) ?? throw new Exception("Deserialized PluginMaster was null.");
            pluginMaster.Sort((pm1, pm2) => string.Compare(pm1.Name, pm2.Name, StringComparison.Ordinal));

            // Set the source for each remote manifest. Allows for checking if is 3rd party.
            foreach (var manifest in pluginMaster)
            {
                manifest.SourceRepo = this;
            }

            var pm = Service<PluginManager>.Get();
            var official = pm.Repos.First();
            Debug.Assert(!official.IsThirdParty, "First repository should be official repository");

            if (official.State == PluginRepositoryState.Success && this.IsThirdParty)
            {
                pluginMaster = pluginMaster.Where(thisRepoEntry =>
                {
                    if (official.PluginMaster!.Any(officialRepoEntry =>
                                                       string.Equals(thisRepoEntry.InternalName, officialRepoEntry.InternalName, StringComparison.InvariantCultureIgnoreCase)))
                    {
                        Log.Warning(
                            "The repository {RepoName} tried to replace the plugin {PluginName}, which is already installed through the official repo - this is no longer allowed for security reasons. " +
                            "Please reach out if you have an use case for this.",
                            this.PluginMasterUrl,
                            thisRepoEntry.InternalName);
                        return false;
                    }

                    return true;
                }).ToList();
            }
            else if (this.IsThirdParty)
            {
                Log.Warning("Official repository not loaded - couldn't check for overrides!");
                this.State = PluginRepositoryState.Fail;
                return;
            }

            this.PluginMaster = pluginMaster.Where(this.IsValidManifest).ToList().AsReadOnly();

            // API9 HACK: Force IsHide to false, we should remove that
            if (!this.IsThirdParty)
            {
                foreach (var manifest in this.PluginMaster)
                {
                    manifest.IsHide = false;
                }
            }

            Log.Information($"Successfully fetched repo: {this.PluginMasterUrl}");
            this.State = PluginRepositoryState.Success;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"PluginMaster failed: {this.PluginMasterUrl}");
            this.State = PluginRepositoryState.Fail;
        }
    }

    private bool IsValidManifest(RemotePluginManifest manifest)
    {
        if (manifest.InternalName.IsNullOrWhitespace())
        {
            Log.Error("Repository at {RepoLink} has a plugin with an invalid InternalName.", this.PluginMasterUrl);
            return false;
        }

        if (manifest.Name.IsNullOrWhitespace())
        {
            Log.Error("Plugin {PluginName} in {RepoLink} has an invalid Name.", manifest.InternalName, this.PluginMasterUrl);
            return false;
        }

        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
        if (manifest.AssemblyVersion == null)
        {
            Log.Error("Plugin {PluginName} in {RepoLink} has an invalid AssemblyVersion.", manifest.InternalName, this.PluginMasterUrl);
            return false;
        }

        if (manifest.TestingAssemblyVersion != null &&
            manifest.TestingAssemblyVersion > manifest.AssemblyVersion &&
            manifest.TestingDalamudApiLevel == null)
        {
            Log.Warning("The plugin {PluginName} in {RepoLink} has a testing version available, but it lacks an associated testing API. The 'TestingDalamudApiLevel' property is required.", manifest.InternalName, this.PluginMasterUrl);
        }

        return true;
    }

    /// <summary>
    ///     配信元が一過性に失敗しただけなら、もう一度だけ取り直す。
    /// </summary>
    /// <remarks>
    ///     2026-09-24 に実測したところ、一部のサードパーティ配信元(puni.sh)は
    ///     応答が中央値 390ms に対して p95 が 6.3 秒、まれに 20 秒のタイムアウトまで振れていた。
    ///     同時刻・同回線で測った GitHub raw が 104 回すべて 73〜102ms だったことから、
    ///     こちら側や回線ではなく配信元の問題で、待てば直る種類のものと判断した。
    ///
    ///     Dalamud は失敗した配信元のプラグインを一覧から丸ごと落とすため、
    ///     この一瞬のぶれがそのまま「ダウンロードが失敗しました。」として利用者に見える。
    ///     1 回取り直すだけでほとんど吸収できる。
    ///
    ///     恒久的な失敗(404 など)は取り直さない。無駄に待たせるだけなので。
    /// </remarks>
    /// <param name="url">取得先。</param>
    /// <returns>pluginmaster の中身。</returns>
    private async Task<string> FetchPluginMasterWithRetryAsync(string url)
    {
        try
        {
            return await this.GetPluginMaster(url);
        }
        catch (Exception ex) when (IsTransientFailure(ex))
        {
            Log.Warning(ex, "Fetch failed, retrying once: {RepoLink}", url);
        }

        await Task.Delay(HttpRetryDelayMilliseconds);

        return await this.GetPluginMaster(url, HttpRetryTimeoutSeconds);
    }

    /// <summary>取り直す価値のある失敗かどうか。</summary>
    private static bool IsTransientFailure(Exception ex) => ex switch
    {
        // 自前のタイムアウト。相手が詰まっている
        OperationCanceledException => true,

        // StatusCode が無い = 接続・DNS・TLS で落ちた。応答すら貰えていない
        HttpRequestException { StatusCode: null } => true,

        // 相手側の一時的な事情。408/429/5xx 以外(404 など)は取り直さない
        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests } => true,
        HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } => true,

        _ => false,
    };

    private async Task<string> GetPluginMaster(string url, int timeout = HttpRequestTimeoutSeconds)
    {
        var httpClient = Service<HappyHttpClient>.Get().SharedHttpClient;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var requestCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));

        using var response = await httpClient.SendAsync(request, requestCts.Token);
        response.EnsureSuccessStatusCode();

        // 本文の読み出しも同じ期限で打ち切る。ヘッダだけ返して本文を流さない相手だと、
        // ここに期限が無いと HttpClient 既定の 100 秒まで待たされる。
        return await response.Content.ReadAsStringAsync(requestCts.Token);
    }
}
