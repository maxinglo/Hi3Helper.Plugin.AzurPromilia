using System;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.Plugin.AzurPromilia;

[GeneratedComClass]
internal sealed partial class AzurPromiliaPluginSelfUpdater : PluginSelfUpdateBase
{
    // Publish formal updates through GitHub Releases and the OSS mirror served by ESA.
    private static readonly string[] ReleaseSources =
    [
        "https://github.com/maxinglo/Hi3Helper.Plugin.AzurPromilia/releases/download/collapse-update",
        "https://plugins.freedorm.net/collapse-plugins/azur-promilia/collapse-update"
    ];

    private readonly Lazy<HttpClient> _updateHttpClient =
        new(() => new PluginHttpClientBuilder().AllowRedirections().Create());

    protected override ReadOnlySpan<string> BaseCdnUrlSpan => ReleaseSources;

    protected override HttpClient UpdateHttpClient => _updateHttpClient.Value;
}
