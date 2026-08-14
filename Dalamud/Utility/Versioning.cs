using System.Linq;
using System.Reflection;

namespace Dalamud.Utility;

/// <summary>
/// Helpers to access Dalamud versioning information.
/// </summary>
internal static class Versioning
{
    private static string? scmVersionInternal;
    private static string? gitHashInternal;
    private static string? gitHashClientStructsInternal;
    private static string? branchInternal;
    private static string? estellBuildDateInternal;

    /// <summary>
    /// Gets the Dalamud version.
    /// </summary>
    /// <returns>The raw Dalamud assembly version.</returns>
    internal static string GetAssemblyVersion() =>
        Assembly.GetAssembly(typeof(Versioning))!.GetName().Version!.ToString();

    /// <summary>
    /// Gets the Dalamud version.
    /// </summary>
    /// <returns>The parsed Dalamud assembly version.</returns>
    internal static Version GetAssemblyVersionParsed() =>
        Assembly.GetAssembly(typeof(Versioning))!.GetName().Version!;

    /// <summary>
    /// Gets the SCM Version from the assembly, or null if it cannot be found. This method will generally return
    /// the <c>git describe</c> output for this build, which will be a raw version if this is a stable build or an
    /// appropriately-annotated version if this is *not* stable. Local builds will return a `Local Build` text string.
    /// </summary>
    /// <returns>The SCM version of the assembly.</returns>
    internal static string GetScmVersion()
    {
        if (scmVersionInternal != null) return scmVersionInternal;

        var asm = typeof(Util).Assembly;
        var attrs = asm.GetCustomAttributes<AssemblyMetadataAttribute>();

        return scmVersionInternal = attrs.First(a => a.Key == "SCMVersion").Value
                                        ?? asm.GetName().Version!.ToString();
    }

    /// <summary>
    /// estell ビルド日(yyyy-MM-dd)を取得する。ビルド時に Dalamud.csproj が
    /// AssemblyMetadata("EstellBuildDate") として埋め込む。
    /// </summary>
    /// <returns>ビルド日。埋め込まれていない場合は "unknown"。</returns>
    internal static string GetEstellBuildDate()
    {
        if (estellBuildDateInternal != null)
            return estellBuildDateInternal;

        var asm = typeof(Util).Assembly;
        var attrs = asm.GetCustomAttributes<AssemblyMetadataAttribute>();

        return estellBuildDateInternal = attrs.FirstOrDefault(a => a.Key == "EstellBuildDate")?.Value ?? "unknown";
    }

    /// <summary>
    /// ゲーム内に表示するバージョン文字列を取得する。SCM バージョンの末尾に
    /// estell ビルド識別子(Estell-Ver-{ビルド日})を付与したもの。
    /// バージョン一致判定には使わず、必ず <see cref="GetScmVersion"/> を使うこと。
    /// </summary>
    /// <returns>表示用のバージョン文字列。</returns>
    internal static string GetDisplayVersion() => $"{GetScmVersion()} Estell-Ver-{GetEstellBuildDate()}";

    /// <summary>
    /// Gets the git commit hash value from the assembly or null if it cannot be found. Will be null for Debug builds,
    /// and will be suffixed with `-dirty` if in release with pending changes.
    /// </summary>
    /// <returns>The git hash of the assembly.</returns>
    internal static string? GetGitHash()
    {
        if (gitHashInternal != null)
            return gitHashInternal;

        var asm = typeof(Util).Assembly;
        var attrs = asm.GetCustomAttributes<AssemblyMetadataAttribute>();

        return gitHashInternal = attrs.FirstOrDefault(a => a.Key == "GitHash")?.Value ?? "N/A";
    }

    /// <summary>
    /// Gets the git hash value from the assembly or null if it cannot be found.
    /// </summary>
    /// <returns>The git hash of the assembly.</returns>
    internal static string? GetGitHashClientStructs()
    {
        if (gitHashClientStructsInternal != null)
            return gitHashClientStructsInternal;

        var asm = typeof(Util).Assembly;
        var attrs = asm.GetCustomAttributes<AssemblyMetadataAttribute>();

        // estell: 本家は First() だったため、メタデータが欠落していると起動時に
        // 未処理例外(Sequence contains no matching element)で落ちる。
        // 他の Get*() と同様に FirstOrDefault + フォールバックにして耐性を持たせる。
        gitHashClientStructsInternal = attrs.FirstOrDefault(a => a.Key == "GitHashClientStructs")?.Value ?? "N/A";

        return gitHashClientStructsInternal;
    }

    /// <summary>
    /// Gets the Git branch name this version of Dalamud was built from, or null, if this is a Debug build.
    /// </summary>
    /// <returns>The branch name.</returns>
    internal static string? GetGitBranch()
    {
        if (branchInternal != null)
            return branchInternal;

        var asm = typeof(Util).Assembly;
        var attrs = asm.GetCustomAttributes<AssemblyMetadataAttribute>();

        var gitBranch = attrs.FirstOrDefault(a => a.Key == "GitBranch")?.Value;
        if (gitBranch == null)
            return null;

        return branchInternal = gitBranch;
    }

    /// <summary>
    /// Gets the active Dalamud track, if this instance was launched through XIVLauncher and used a version
    /// downloaded from webservices.
    /// </summary>
    /// <returns>The name of the track, or null.</returns>
    internal static string? GetActiveTrack()
    {
        return Environment.GetEnvironmentVariable("DALAMUD_BRANCH");
    }
}
