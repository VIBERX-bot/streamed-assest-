using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.GameTypes.FN.Assets.Exports.Bundles;
using CUE4Parse.GameTypes.FN.Assets.Exports.DataAssets;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using EpicManifestParser;
using EpicManifestParser.Api;
using EpicManifestParser.UE;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using SkiaSharp;

namespace ShopStreamExtractor;

internal static class Program
{
    private const string ToolVersion = "66";
    private const int CloudSignatureVersion = 2;
    // fortnitePS4USGameClient. Unlike the retired iOS client, this client supports device-code login.
    private const string FortniteBasic = "ZDg1NjZmMmU3ZjVjNDhmODk2ODMxNzNlYjUyOWZlZTE6MjU1YzcxMDljODI3NDI0MTk4NjYxNmUzNzAyNjc4YjU=";
    private const string LauncherBasicFallback = "MzRhMDJjZjhmNDQxNGUyOWIxNTkyMTg3NmRhMzZmOWE6ZGFhZmJjY2M3Mzc3NDUwMzlkZmZlNTNkOTRmYzc2Y2Y=";
    private static string LauncherBasic => Environment.GetEnvironmentVariable("EPIC_LAUNCHER_BASIC")?.Trim() ?? LauncherBasicFallback;
    private const string TokenUrl = "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token";
    private const string DeviceAuthorizationUrl = "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/deviceAuthorization";
    private const string CatalogUrl = "https://fngw-mcp-gc-livefn.ol.epicgames.com/fortnite/api/storefront/v2/catalog";
    private const string ManifestUrl = "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/v2/platform/Windows/namespace/fn/catalogItem/4fe75bbc5a674f4f9b356b5c90567da5/app/Fortnite/label/Live";
    private const string StudioManifestsUrl = "https://export-service-new.dillyapis.com/v1/manifests";
    private const string VersionUrl = "https://api.fortniteporting.app/v2/fortnite/versions/latest";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static string? LastLiveManifestUrl;
    private static string? LastLiveManifestSha256;
    private static long LastLiveManifestSize;
    private static bool CloudCacheNeedsSignatureUpgrade;
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly string OutputDir = Path.Combine(AppContext.BaseDirectory, "output");
    private static readonly string UpdatesDir = Path.Combine(OutputDir, "updates");
    private static readonly string AuthFile = Path.Combine(DataDir, "login-session.bin");
    private static readonly string SnapshotFile = Path.Combine(DataDir, "catalog-snapshot.json");
    private static readonly string SeenDisplayAssetsFile = Path.Combine(DataDir, "seen-display-assets.json");
    private static readonly string SeenDav2PathsFile = Path.Combine(DataDir, "seen-dav2-paths.json");
    private static readonly string SeenCatalogOffersFile = Path.Combine(DataDir, "seen-catalog-offers.json");
    private static readonly string CloudDisplayAssetCacheFile = Path.Combine(DataDir, "cloud-display-assets.json");
    private static readonly string SeenRenderImageHashesFile = Path.Combine(DataDir, "seen-render-image-hashes.json");
    private static readonly string WebhookFile = Path.Combine(AppContext.BaseDirectory, "discord-webhook.txt");
    private static readonly string WatermarkFile = Path.Combine(AppContext.BaseDirectory, "watermark.png");
    private static readonly HashSet<string> TrackedCosmeticTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AthenaCharacter", "AthenaBackpack", "AthenaPickaxe", "AthenaDance",
        "AthenaGlider", "AthenaItemWrap", "AthenaSkyDiveContrail", "CosmeticShoes",
        "AthenaLoadingScreen", "AthenaMusicPack", "AthenaPet", "AthenaEmoji",
        "AthenaSpray", "AthenaToy", "CosmeticMimosa", "Sparks"
    };
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"Shop Stream Extractor v{ToolVersion}");
        // Use the managed AssetRipper decoders for BC7/BC6/ETC textures. The native
        // Detex path is not initialized in this self-contained console application.
        TextureDecoder.UseAssetRipperTextureDecoder = true;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();
        Directory.CreateDirectory(DataDir); Directory.CreateDirectory(OutputDir); Directory.CreateDirectory(UpdatesDir);
        try
        {
            var session = await LoadOrLoginAsync();
            Console.WriteLine("تم تسجيل الدخول. جاري تجهيز ملفات Fortnite On-Demand...");
            var provider = await CreateProviderAsync();
            var providerCheckedAt = DateTimeOffset.UtcNow;
            var seenDav2Paths = await LoadSeenDav2PathsAsync();
            var seenRenderImageHashes = await LoadSeenRenderImageHashesAsync();
            Console.WriteLine($"Fortnite Live Manifest endpoint: {LastLiveManifestUrl ?? "غير معروف"}");
            if (!string.IsNullOrWhiteSpace(LastLiveManifestSha256))
                Console.WriteLine($"Live Manifest SHA256: {LastLiveManifestSha256}");
            var testMode = args.Contains("--test", StringComparer.OrdinalIgnoreCase) ||
                           args.Contains("--test-dav2", StringComparer.OrdinalIgnoreCase);
            var runOnce = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
            if (testMode)
                Console.WriteLine("اختبار DAv2 العشوائي: سيتم تصدير 50 Render Image ناجحة إلى PNG.");

            while (true)
            {
                try
                {
                    if (session.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(10)) session = await RefreshSessionAsync(session);
                    var extracted = new List<ExtractedOfferArtifact>();
                    var pendingRenderImageHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!testMode && DateTimeOffset.UtcNow - providerCheckedAt >= TimeSpan.FromMinutes(2))
                    {
                        providerCheckedAt = DateTimeOffset.UtcNow;
                        Console.WriteLine("جاري فحص إصدار Fortnite Live لدى Epic...");
                        if (await HasLiveManifestChangedAsync())
                        {
                            Console.WriteLine("تم اكتشاف Live Manifest جديد؛ جاري إعادة بناء الفهرس...");
                            provider = await CreateProviderAsync();
                        }
                        else
                        {
                            Console.WriteLine(
                                "Live Manifest لم يتغير؛ جاري تحديث Cloud Manifest وفهرس streamed assets...");
                            provider = await CreateProviderAsync();
                        }
                    }

                    if (testMode)
                    {
                        var randomDav2Paths = BuildCloudDisplayAssetIndex(provider).Keys
                            .OrderBy(_ => Random.Shared.NextInt64())
                            .ToArray();
                        Console.WriteLine($"تم العثور على {randomDav2Paths.Length} مسار DAv2؛ بدأت العينة العشوائية.");
                        foreach (var dav2Path in randomDav2Paths)
                        {
                            var entry = CreateCloudCatalogEntry(dav2Path);
                            var artifact = await ExtractOfferAsync(
                                provider, entry, "MountedDAv2", "test");
                            if (artifact is not null) extracted.Add(artifact);
                            if (extracted.Count >= 50) break;
                        }
                        Console.WriteLine($"اختبار DAv2: تم تصدير {extracted.Count} من أصل 50 صورة PNG مطلوبة.");
                    }
                    else
                    {
                        var currentDav2Paths = BuildCloudDisplayAssetIndex(provider).Keys
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        string[] changes;
                        if (seenDav2Paths is null)
                        {
                            seenDav2Paths = new HashSet<string>(
                                currentDav2Paths, StringComparer.OrdinalIgnoreCase);
                            await SaveSeenDav2PathsAsync(seenDav2Paths);
                            Console.WriteLine(
                                $"تم حفظ {seenDav2Paths.Count} مسار DAv2 كخط أساس دون استخراج القديم.");
                            changes = [];
                        }
                        else
                        {
                            changes = currentDav2Paths
                                .Where(path => !seenDav2Paths.Contains(path))
                                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                        }

                        if (changes.Length > 0)
                            Console.WriteLine($"تم اكتشاف {changes.Length} مسار DAv2 جديد بعد تحديث Manifests.");

                        var successfullyRenderedPaths = new List<string>();
                        foreach (var dav2Path in changes)
                        {
                            var entry = CreateCloudCatalogEntry(dav2Path);
                            var artifact = await ExtractOfferAsync(
                                provider, entry, "MountedDAv2", "new");
                            if (artifact is null) continue;
                            successfullyRenderedPaths.Add(dav2Path);
                            var imageHash = await CalculateFileSha256Async(artifact.ImagePath);
                            if (seenRenderImageHashes.Contains(imageHash) ||
                                pendingRenderImageHashes.Values.Contains(imageHash, StringComparer.Ordinal))
                            {
                                Console.WriteLine($"تم تجاهل صورة مكررة لمسار DisplayAsset جديد: {artifact.DisplayAssetName}");
                                continue;
                            }
                            extracted.Add(artifact);
                            pendingRenderImageHashes[artifact.ImagePath] = imageHash;
                        }

                        if (extracted.Count > 0)
                        {
                            await PublishCatalogUpdateAsync(extracted, false);
                            foreach (var imageHash in pendingRenderImageHashes.Values)
                                seenRenderImageHashes.Add(imageHash);
                            await SaveSeenRenderImageHashesAsync(seenRenderImageHashes);
                        }

                        foreach (var displayAssetPath in successfullyRenderedPaths)
                            seenDav2Paths.Add(displayAssetPath);
                        if (successfullyRenderedPaths.Count > 0)
                            await SaveSeenDav2PathsAsync(seenDav2Paths);
                    }
                    if (testMode && extracted.Count > 0)
                        await PublishCatalogUpdateAsync(extracted, testMode);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] تمت مراجعة DAv2. المسارات المحفوظة: {seenDav2Paths?.Count ?? 0}");
                    if (runOnce)
                    {
                        Console.WriteLine("انتهى التشغيل التجريبي لمرة واحدة. يمكنك إغلاق النافذة.");
                        return;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"خطأ أثناء المراقبة:\n{e}");
                    await File.WriteAllTextAsync(Path.Combine(DataDir, "monitor-error.txt"), e.ToString());
                    if (runOnce)
                    {
                        Console.WriteLine("انتهى الاختبار بعد الخطأ ولن يُعاد إرسال دفعة عشوائية.");
                        return;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e); Console.ReadKey(); }
    }

    private static async Task<EpicSession> LoadOrLoginAsync()
    {
        if (File.Exists(AuthFile))
        {
            try
            {
                var encrypted = await File.ReadAllBytesAsync(AuthFile);
                var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
                var saved = JsonConvert.DeserializeObject<EpicSession>(json)!;
                return saved.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10) ? saved : await RefreshSessionAsync(saved);
            }
            catch (Exception e)
            {
                Console.WriteLine($"تعذر تجديد تسجيل الدخول المحفوظ ({e.Message}). سنسجل الدخول مجددًا.");
                File.Delete(AuthFile);
            }
        }

        // Epic requires an application access token for this endpoint, not Basic auth directly.
        var clientSession = await PostTokenAsync(new()
        { ["grant_type"] = "client_credentials", ["token_type"] = "eg1" });
        using var start = new HttpRequestMessage(HttpMethod.Post, DeviceAuthorizationUrl);
        start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clientSession.AccessToken);
        start.Content = new FormUrlEncodedContent([]);
        var startResponse = await Http.SendAsync(start);
        var startBody = await startResponse.Content.ReadAsStringAsync();
        if (!startResponse.IsSuccessStatusCode) throw new Exception($"فشل بدء تسجيل الدخول: {startBody}");
        var device = JsonConvert.DeserializeObject<DeviceCodeResponse>(startBody)!;
        var loginUrl = device.VerificationUriComplete ?? device.VerificationUri;
        Console.WriteLine($"\nافتح الرابط وسجل الدخول إلى Epic:\n{loginUrl}");
        Console.WriteLine($"كود الجهاز: {device.UserCode}\nبانتظار موافقتك في المتصفح...");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);
        var delay = Math.Max(device.Interval, 5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(delay));
            var (session, error) = await TryPostTokenAsync(new()
            {
                ["grant_type"] = "device_code", ["device_code"] = device.DeviceCode,
                ["token_type"] = "eg1"
            });
            if (session is not null) { await SaveSessionAsync(session); return session; }
            if (error.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase)) continue;
            if (error.Contains("slow_down", StringComparison.OrdinalIgnoreCase)) { delay += 5; continue; }
            if (error.Contains("expired", StringComparison.OrdinalIgnoreCase)) break;
            throw new Exception($"فشل تسجيل الدخول: {error}");
        }
        throw new Exception("انتهت مهلة كود الجهاز. شغّل الأداة مرة أخرى للحصول على كود جديد.");
    }

    private static async Task<EpicSession> RefreshSessionAsync(EpicSession old)
    {
        if (string.IsNullOrWhiteSpace(old.RefreshToken)) throw new Exception("لا يوجد refresh token محفوظ.");
        var session = await PostTokenAsync(new()
        { ["grant_type"]="refresh_token", ["refresh_token"]=old.RefreshToken, ["token_type"]="eg1" });
        await SaveSessionAsync(session);
        return session;
    }

    private static async Task SaveSessionAsync(EpicSession session)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(session));
        await File.WriteAllBytesAsync(AuthFile, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
    }

    private static async Task<(EpicSession? Session, string Error)> TryPostTokenAsync(Dictionary<string,string> values)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(values) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", FortniteBasic);
        var res = await Http.SendAsync(req); var text = await res.Content.ReadAsStringAsync();
        return res.IsSuccessStatusCode ? (JsonConvert.DeserializeObject<EpicSession>(text), "") : (null, text);
    }

    private static async Task<EpicSession> PostTokenAsync(Dictionary<string,string> values)
    {
        var (session, error) = await TryPostTokenAsync(values);
        return session ?? throw new Exception(error);
    }

    private static async Task<EpicSession> PostTokenWithBasicAsync(Dictionary<string,string> values, string basic)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(values) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        var res = await Http.SendAsync(req); var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"فشل توثيق خدمة Launcher: {text}");
        return JsonConvert.DeserializeObject<EpicSession>(text)!;
    }

    private static async Task<EpicSession> GetLauncherSessionAsync()
    {
        return await PostTokenWithBasicAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["token_type"] = "eg1"
        }, LauncherBasic);
    }

    private static async Task<byte[]> GetLiveManifestInfoBytesAsync(EpicSession launcherSession)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", launcherSession.AccessToken);
            req.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            req.Headers.TryAddWithoutValidation("Pragma", "no-cache");
            req.Headers.TryAddWithoutValidation("X-EpicGames-Language", "en");

            using var res = await Http.SendAsync(req);
            var bytes = await res.Content.ReadAsByteArrayAsync();

            if (res.IsSuccessStatusCode)
            {
                Console.WriteLine($"تم جلب Fortnite Live Manifest من Epic Launcher API ({bytes.Length:N0} bytes).");
                return bytes;
            }

            // The application token is independent from the Fortnite user session.
            // If Epic rejects it, request a fresh launcher token and retry once.
            if ((int)res.StatusCode == 401 && attempt == 0)
            {
                Console.WriteLine("انتهت صلاحية Launcher access token، جاري طلب Token جديد...");
                launcherSession = await GetLauncherSessionAsync();
                continue;
            }

            var errorText = Encoding.UTF8.GetString(bytes);
            throw new HttpRequestException(
                $"فشل جلب Fortnite Live Manifest من Launcher API: {(int)res.StatusCode} {res.ReasonPhrase}\n" +
                errorText[..Math.Min(errorText.Length, 2000)]);
        }

        throw new InvalidOperationException("تعذر جلب Fortnite Live Manifest.");
    }

    private static async Task<bool> HasLiveManifestChangedAsync()
    {
        var launcherSession = await GetLauncherSessionAsync();
        var manifestInfoBytes = await GetLiveManifestInfoBytesAsync(launcherSession);
        var sha256 = Convert.ToHexString(SHA256.HashData(manifestInfoBytes));
        return !string.Equals(sha256, LastLiveManifestSha256, StringComparison.Ordinal);
    }

    private static async Task<HybridFileProvider> CreateProviderAsync()
    {
        var chunksDirectory = Path.Combine(DataDir, "chunks");
        var manifestDirectory = Path.Combine(DataDir, "manifest");
        Directory.CreateDirectory(chunksDirectory);
        Directory.CreateDirectory(manifestDirectory);

        var version = await GetJsonAsync<VersionInfo>(VersionUrl);
        var launcherSession = await GetLauncherSessionAsync();

        // -----------------------------------------------------------------
        // 1. Fetch the CURRENT Fortnite Live manifest directly from Epic.
        //    We intentionally do not read the installed game's
        //    Cloud/cloudcontent.json as an authority.
        // -----------------------------------------------------------------
        var manifestInfoBytes = await GetLiveManifestInfoBytesAsync(launcherSession);
        Console.WriteLine($"Live Manifest size: {manifestInfoBytes.Length:N0} bytes");
        Console.WriteLine($"Live Manifest SHA256: {Convert.ToHexString(SHA256.HashData(manifestInfoBytes))}");

        var manifestInfoJson = Encoding.UTF8.GetString(manifestInfoBytes);
        if (!manifestInfoJson.Contains("\"elements\"", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"خدمة Launcher لم تُرجع Manifest صالحًا: {manifestInfoJson[..Math.Min(manifestInfoJson.Length, 1200)]}");

        var manifestInfo = ManifestInfo.Deserialize(manifestInfoBytes);
        var options = new ManifestParseOptions
        {
            ChunkBaseUrl = "https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir/",
            ChunkCacheDirectory = chunksDirectory,
            ManifestCacheDirectory = manifestDirectory,
            CacheChunksAsIs = false
        };

        var (manifest, _) = await manifestInfo.DownloadAndParseAsync(options);
        Console.WriteLine($"Fortnite Live Manifest parsed: {manifest.Files.Count:N0} files");

        // The Epic Live BuildPatch manifest is authoritative. Register its normal
        // archives first; never read the installed Cloud/cloudcontent.json.
        var liveManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestInfoBytes));

        // The INI can move between directories across Fortnite builds, so locate
        // it by filename instead of requiring a literal Cloud/ prefix.
        var iniFile = manifest.Files.FirstOrDefault(x =>
            Path.GetFileName(x.FileName.Replace('\\', '/')).Equals(
                "IoStoreOnDemand.ini", StringComparison.OrdinalIgnoreCase));

        string? onDemandIni = null;
        string? liveTocPath = null;
        var distributionUrl = "https://egdownload.fastly-edge.com/";
        if (iniFile is not null)
        {
            using var reader = new StreamReader(iniFile.GetStream());
            onDemandIni = await reader.ReadToEndAsync();
            await File.WriteAllTextAsync(Path.Combine(DataDir, "IoStoreOnDemand.ini"), onDemandIni);
            liveTocPath = ParseOnDemandIniValue(onDemandIni, "TocPath");
            distributionUrl = ParseOnDemandIniValue(onDemandIni, "DistributionUrl")
                              ?? distributionUrl;
            Console.WriteLine($"تم العثور على IoStoreOnDemand.ini: {iniFile.FileName}");
        }
        else
        {
            var relatedFiles = manifest.Files
                .Select(x => x.FileName)
                .Where(x => x.Contains("ondemand", StringComparison.OrdinalIgnoreCase) ||
                            x.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await File.WriteAllLinesAsync(
                Path.Combine(DataDir, "manifest-ondemand-candidates.txt"), relatedFiles);
            Console.WriteLine(
                "تحذير: لم يوجد IoStoreOnDemand.ini كملف مستقل؛ سيتم استخدام TOCs المضمنة في Live Manifest.");
        }
        var distributionUri = new Uri(distributionUrl.TrimEnd('/') + "/");

        var provider = new HybridFileProvider(new VersionContainer(EGame.GAME_UE6_0), chunksDirectory)
        {
            SkipReferencedTextures = false,
            OnDemandOptions = new IoStoreOnDemandOptions
            {
                ChunkHostUri = distributionUri,
                ChunkCacheDirectory = new DirectoryInfo(chunksDirectory),
                Authorization = null,
                Timeout = TimeSpan.FromMinutes(5)
            }
        };

        var liveArchiveCount = await provider.RegisterManifestAsync(manifest);
        var embeddedLiveTocCount = await RegisterOnDemandTocsAsync(
            provider, manifest, "Fortnite Live Embedded");
        Console.WriteLine($"تم تسجيل {liveArchiveCount} حاوية من Fortnite Live Manifest.");

        Console.WriteLine($"On-Demand DistributionUrl: {distributionUri}");
        var downloadedLiveTocCount = 0;
        if (!string.IsNullOrWhiteSpace(liveTocPath))
        {
            var liveTocUri = Uri.TryCreate(liveTocPath, UriKind.Absolute, out var absoluteTocUri)
                ? absoluteTocUri
                : new Uri(distributionUri, liveTocPath.TrimStart('/'));
            Console.WriteLine($"Latest Live TocPath: {liveTocPath}");

            using var tocRequest = new HttpRequestMessage(HttpMethod.Get, liveTocUri);
            tocRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            tocRequest.Headers.TryAddWithoutValidation("Pragma", "no-cache");
            using var tocResponse = await Http.SendAsync(tocRequest);
            var liveTocBytes = await tocResponse.Content.ReadAsByteArrayAsync();
            if (!tocResponse.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"فشل تنزيل أحدث Live On-Demand TOC: {(int)tocResponse.StatusCode} " +
                    $"{tocResponse.ReasonPhrase}");
            if (liveTocBytes.Length == 0)
                throw new InvalidDataException("أحدث Live On-Demand TOC فارغ.");

            await provider.RegisterOnDemandAsync(
                liveTocBytes, Path.GetFileName(liveTocUri.LocalPath), "Fortnite Live Latest");
            await File.WriteAllTextAsync(Path.Combine(DataDir, "latest-live-toc.txt"),
                $"TocPath={liveTocPath}\nBytes={liveTocBytes.Length}\n" +
                $"SHA256={Convert.ToHexString(SHA256.HashData(liveTocBytes))}\n");
            downloadedLiveTocCount = 1;
            Console.WriteLine($"تم تسجيل أحدث Live TOC ({liveTocBytes.Length:N0} bytes).");
        }
        else if (iniFile is not null)
        {
            Console.WriteLine(
                "تحذير: IoStoreOnDemand.ini لا يحتوي TocPath؛ سيتم استخدام TOCs المضمنة.");
        }
        var liveTocCount = embeddedLiveTocCount + downloadedLiveTocCount;

        // Studio augments the Epic Live index, but an outage of its third-party
        // discovery endpoint must not prevent Live assets from being processed.
        FBuildPatchAppManifest? studioManifest = null;
        var studioArchiveCount = 0;
        var studioTocCount = 0;
        try
        {
            using var studioIndexRequest = new HttpRequestMessage(HttpMethod.Get, StudioManifestsUrl);
            studioIndexRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var studioIndexResponse = await Http.SendAsync(studioIndexRequest);
            var studioIndexJson = await studioIndexResponse.Content.ReadAsStringAsync();
            if (!studioIndexResponse.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"فشل جلب قائمة Fortnite_Studio manifests: {(int)studioIndexResponse.StatusCode} " +
                    $"{studioIndexResponse.ReasonPhrase}\n{studioIndexJson[..Math.Min(studioIndexJson.Length, 2000)]}");

            var studioEntry = JArray.Parse(studioIndexJson)
                .OfType<JObject>()
                .FirstOrDefault(x => string.Equals(
                    x.GetValue("AppName", StringComparison.OrdinalIgnoreCase)?.Value<string>(),
                    "Fortnite_Studio", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("لم يوجد Fortnite_Studio manifest في خدمة manifests.");
            var studioManifestUrl = studioEntry
                .GetValue("DownloadUrl", StringComparison.OrdinalIgnoreCase)?.Value<string>()
                ?? throw new InvalidDataException("Fortnite_Studio manifest لا يحتوي downloadUrl.");

            using var studioRequest = new HttpRequestMessage(HttpMethod.Get, studioManifestUrl);
            studioRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var studioResponse = await Http.SendAsync(studioRequest);
            var studioBytes = await studioResponse.Content.ReadAsByteArrayAsync();
            if (!studioResponse.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"فشل تنزيل Fortnite_Studio manifest: {(int)studioResponse.StatusCode} {studioResponse.ReasonPhrase}");

            studioManifest = FBuildPatchAppManifest.Deserialize(studioBytes, options);
            studioArchiveCount = await provider.RegisterManifestAsync(studioManifest);
            studioTocCount = await RegisterOnDemandTocsAsync(provider, studioManifest, "Fortnite_Studio");
            Console.WriteLine($"تم تسجيل {studioArchiveCount} حاوية و{studioTocCount} On-Demand TOC من Fortnite_Studio.");
        }
        catch (Exception error)
        {
            Console.WriteLine($"تحذير: تعذر تجهيز Fortnite_Studio؛ سيستمر التشغيل ببيانات Epic Live. {error.Message}");
        }

        if (liveTocCount + studioTocCount == 0)
            throw new InvalidDataException("لم يوجد أي .uondemandtoc في Fortnite Live أو Fortnite_Studio manifests.");

        var mountedArchives = await provider.MountAsync();
        await SubmitFortniteKeysAsync(provider, version);
        var cloudMount = await MountLocalCloudArchivesAsync(
            provider, version, chunksDirectory, manifestDirectory);
        provider.LoadVirtualPaths();
        provider.PostMount();

        var displayAssetPaths = provider.Files.Keys
            .Where(x => x.Replace('\\', '/').Contains(
                "/NewDisplayAssets/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dAv2Paths = displayAssetPaths.Where(IsCloudDisplayAssetVirtualPath).ToArray();
        await File.WriteAllLinesAsync(Path.Combine(DataDir, "new-display-assets.txt"), displayAssetPaths);
        await File.WriteAllLinesAsync(Path.Combine(DataDir, "new-display-assets-dav2.txt"), dAv2Paths);
        var offerCatalogTexturePaths = provider.Files.Keys
            .Where(x => x.Replace('\\', '/').Contains(
                "/OfferCatalog/Textures/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await File.WriteAllLinesAsync(
            Path.Combine(DataDir, "offercatalog-textures.txt"), offerCatalogTexturePaths);
        await File.WriteAllTextAsync(Path.Combine(DataDir, "provider-stats.txt"),
            $"Live manifest files: {manifest.Files.Count}\n" +
            $"Live archives registered: {liveArchiveCount}\n" +
            $"Live on-demand TOCs: {liveTocCount}\n" +
            $"Studio manifest files: {studioManifest?.Files.Count ?? 0}\n" +
            $"Studio archives registered: {studioArchiveCount}\n" +
            $"Studio on-demand TOCs: {studioTocCount}\n" +
            $"Mounted archives: {mountedArchives}\n" +
            $"Cloud archives registered: {cloudMount.Registered}\n" +
            $"Cloud archives mounted: {cloudMount.Mounted}\n" +
            $"Cloud Manifest: {cloudMount.ManifestUrl ?? "missing"}\n" +
            $"Provider files: {provider.Files.Count}\n" +
            $"NewDisplayAssets: {displayAssetPaths.Length}\n" +
            $"DAv2 uasset files: {dAv2Paths.Length}\n" +
            $"OfferCatalog texture files: {offerCatalogTexturePaths.Length}\n" +
            $"IoStoreOnDemand.ini: {(iniFile is null ? "missing" : "found")}\n" +
            $"TocPath: {liveTocPath ?? "missing; embedded TOC fallback"}\n");

        Console.WriteLine(
            $"الفهرس النهائي: {provider.Files.Count:N0} ملف، " +
            $"NewDisplayAssets={displayAssetPaths.Length:N0}، DAv2={dAv2Paths.Length:N0}.");

        var mappingsPath = Path.Combine(DataDir, "mappings.usmap");
        await File.WriteAllBytesAsync(mappingsPath, await Http.GetByteArrayAsync(version.Mappings.Url));
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingsPath, StringComparer.Ordinal);

        // Publish the revision only after the provider is completely usable. If
        // construction fails, the next polling pass will retry this manifest.
        LastLiveManifestUrl = ManifestUrl;
        LastLiveManifestSize = manifestInfoBytes.Length;
        LastLiveManifestSha256 = liveManifestSha256;
        return provider;

#if false // Retained temporarily for reference: obsolete cloudcontent.json/TocPath flow.

        // -----------------------------------------------------------------
        // 2. The remote Live manifest itself contains Cloud/cloudcontent.json.
        //    Read that copy and use its ManifestPath. This is the important
        //    difference from the old code: no installed/local Cloud file is
        //    consulted to decide which Cloud manifest is current.
        // -----------------------------------------------------------------
        var embeddedCloudContent = manifest.Files.FirstOrDefault(x =>
            x.FileName.Replace('\\', '/').EndsWith("Cloud/cloudcontent.json", StringComparison.OrdinalIgnoreCase));

        if (embeddedCloudContent is null)
            throw new FileNotFoundException(
                "لم يوجد Cloud/cloudcontent.json داخل Fortnite Live Manifest الذي جلبناه من Epic.");

        string cloudContentJson;
        using (var cloudContentReader = new StreamReader(embeddedCloudContent.GetStream()))
            cloudContentJson = await cloudContentReader.ReadToEndAsync();

        var cloudContent = JsonConvert.DeserializeObject<CloudContentInfo>(cloudContentJson)
            ?? throw new InvalidDataException("تعذر تحليل Cloud/cloudcontent.json القادم من Epic.");

        if (string.IsNullOrWhiteSpace(cloudContent.ManifestPath))
            throw new InvalidDataException("Cloud/cloudcontent.json القادم من Epic لا يحتوي ManifestPath.");

        var cloudManifestUrl = GetCloudManifestUrl(cloudContent.ManifestPath);
        LastCloudManifestUrl = cloudManifestUrl;

        Console.WriteLine("----------------------------------------");
        Console.WriteLine("Epic Cloud Manifest");
        Console.WriteLine($"ManifestPath: {cloudContent.ManifestPath}");
        Console.WriteLine($"URL: {cloudManifestUrl}");

        // Always download the remote BuildPatch manifest with no-cache headers.
        using var cloudRequest = new HttpRequestMessage(HttpMethod.Get, cloudManifestUrl);
        cloudRequest.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        cloudRequest.Headers.TryAddWithoutValidation("Pragma", "no-cache");

        using var cloudResponse = await Http.SendAsync(cloudRequest);
        var cloudManifestBytes = await cloudResponse.Content.ReadAsByteArrayAsync();
        if (!cloudResponse.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(cloudManifestBytes);
            throw new HttpRequestException(
                $"فشل تنزيل Epic Cloud Manifest: {(int)cloudResponse.StatusCode} {cloudResponse.ReasonPhrase}\n" +
                text[..Math.Min(text.Length, 2000)]);
        }

        LastCloudManifestSize = cloudManifestBytes.Length;
        LastCloudManifestSha256 = Convert.ToHexString(SHA256.HashData(cloudManifestBytes));
        Console.WriteLine($"Manifest size: {LastCloudManifestSize:N0} bytes");
        Console.WriteLine($"Manifest SHA256: {LastCloudManifestSha256}");

        var cloudManifestUri = new Uri(cloudManifestUrl);
        var cloudChunkBaseUrl = new Uri(cloudManifestUri, ".").AbsoluteUri;
        Console.WriteLine($"Cloud chunks base URL: {cloudChunkBaseUrl}");

        var cloudOptions = new ManifestParseOptions
        {
            ChunkBaseUrl = cloudChunkBaseUrl,
            ChunkCacheDirectory = chunksDirectory,
            ManifestCacheDirectory = manifestDirectory,
            CacheChunksAsIs = false
        };
        var cloudManifest = FBuildPatchAppManifest.Deserialize(cloudManifestBytes, cloudOptions);

        // -----------------------------------------------------------------
        // 3. Register the normal Fortnite Live manifest first.
        // -----------------------------------------------------------------
        var liveArchiveCount = await provider.RegisterManifestAsync(manifest);
        Console.WriteLine($"تم تسجيل {liveArchiveCount} حاوية من Fortnite Live Manifest.");

        // -----------------------------------------------------------------
        // 4. Register the CURRENT Cloud BuildPatch manifest second.
        //    This makes Cloud containers available as the newer layer.
        // -----------------------------------------------------------------
        var cloudArchiveCount = await provider.RegisterManifestAsync(cloudManifest);
        Console.WriteLine($"تم تسجيل {cloudArchiveCount} حاوية من Epic Cloud Manifest.");

        // -----------------------------------------------------------------
        // 5. ONLY NOW register On-Demand TOCs.
        //    This deliberately puts the live .uondemandtoc after the Cloud
        //    manifest registration, as requested, while still registering it
        //    before the final MountAsync that consumes the TOC indexes.
        // -----------------------------------------------------------------
        var embeddedTocs = manifest.Files
            .Where(x => x.FileName.EndsWith(".uondemandtoc", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => Path.GetFileName(x.FileName)
                .Equals("global.uondemandtoc", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (embeddedTocs.Length > 0)
        {
            Console.WriteLine($"تم العثور على {embeddedTocs.Length} ملف .uondemandtoc داخل Live Manifest.");
            foreach (var toc in embeddedTocs)
            {
                Console.WriteLine($"تسجيل Embedded TOC بعد Cloud Manifest: {toc.FileName}");
                using var tocStream = toc.GetStream();
                using var memory = new MemoryStream();
                await tocStream.CopyToAsync(memory);
                var tocBytes = memory.ToArray();
                Console.WriteLine($"  TOC size: {tocBytes.Length:N0} bytes | SHA256: {Convert.ToHexString(SHA256.HashData(tocBytes))}");
                await provider.RegisterOnDemandAsync(tocBytes, Path.GetFileName(toc.FileName));
            }
        }

        // -----------------------------------------------------------------
        // 6. Read IoStoreOnDemand.ini from the CURRENT remote Live manifest
        //    and fetch its TocPath with no-cache headers. This is the live
        //    TOC Epic can change independently of the launcher manifest.
        // -----------------------------------------------------------------
        var iniFile = manifest.Files.FirstOrDefault(x =>
            Path.GetFileName(x.FileName).Equals("IoStoreOnDemand.ini", StringComparison.OrdinalIgnoreCase));

        string? liveTocPath = null;
        if (iniFile is not null)
        {
            using var reader = new StreamReader(iniFile.GetStream());
            var ini = await reader.ReadToEndAsync();
            await File.WriteAllTextAsync(Path.Combine(DataDir, "IoStoreOnDemand.ini"), ini);

            liveTocPath = ini.Split('\n')
                .Select(x => x.Trim())
                .Where(x => !x.StartsWith(";", StringComparison.Ordinal) &&
                            !x.StartsWith("#", StringComparison.Ordinal))
                .Select(x => x.TrimEnd('\r'))
                .FirstOrDefault(x => x.StartsWith("TocPath=", StringComparison.OrdinalIgnoreCase))
                ?.Split('=', 2)[1].Trim().Trim('"');

            Console.WriteLine("IoStoreOnDemand.ini: موجود");
            Console.WriteLine($"TocPath: {liveTocPath ?? "غير موجود"}");

            if (!string.IsNullOrWhiteSpace(liveTocPath))
            {
                var liveTocUrl = liveTocPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? liveTocPath
                    : "https://egdownload.fastly-edge.com/" + liveTocPath.TrimStart('/');

                Console.WriteLine($"Live TOC URL: {liveTocUrl}");

                using var liveTocRequest = new HttpRequestMessage(HttpMethod.Get, liveTocUrl);
                liveTocRequest.Headers.CacheControl = new CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true
                };
                liveTocRequest.Headers.TryAddWithoutValidation("Pragma", "no-cache");

                using var liveTocResponse = await Http.SendAsync(liveTocRequest);
                var liveTocBytes = await liveTocResponse.Content.ReadAsByteArrayAsync();
                if (!liveTocResponse.IsSuccessStatusCode)
                {
                    var text = Encoding.UTF8.GetString(liveTocBytes);
                    throw new HttpRequestException(
                        $"فشل تنزيل Live .uondemandtoc: {(int)liveTocResponse.StatusCode} {liveTocResponse.ReasonPhrase}\n" +
                        text[..Math.Min(text.Length, 2000)]);
                }

                Console.WriteLine($"Live .uondemandtoc size: {liveTocBytes.Length:N0} bytes");
                Console.WriteLine($"Live .uondemandtoc SHA256: {Convert.ToHexString(SHA256.HashData(liveTocBytes))}");
                await File.WriteAllBytesAsync(
                    Path.Combine(DataDir, Path.GetFileName(liveTocPath)), liveTocBytes);

                // Register LAST. This is the live TOC that should win over stale
                // embedded TOCs with the same package information.
                await provider.RegisterOnDemandAsync(liveTocBytes, Path.GetFileName(liveTocPath));
                Console.WriteLine($"تم تسجيل Live TOC بعد Cloud Manifest: {Path.GetFileName(liveTocPath)}");
            }
        }
        else
        {
            Console.WriteLine("تحذير: IoStoreOnDemand.ini غير موجود داخل Live Manifest.");
        }

        if (embeddedTocs.Length == 0 && string.IsNullOrWhiteSpace(liveTocPath))
            throw new Exception("لم يوجد أي .uondemandtoc صالح بعد تسجيل Epic Cloud Manifest.");

        // -----------------------------------------------------------------
        // 7. Mount everything AFTER the Cloud manifest and live TOC have been
        //    registered. Then submit keys and rebuild the virtual path index.
        // -----------------------------------------------------------------
        var mountedArchives = await provider.MountAsync();
        await SubmitFortniteKeysAsync(provider, version);

        var totalFilesAfterMount = provider.Files.Count;
        Console.WriteLine("----------------------------------------");
        Console.WriteLine($"Mounted archives: {mountedArchives:N0}");
        Console.WriteLine($"Provider files after mount: {totalFilesAfterMount:N0}");
        Console.WriteLine($"NewDisplayAssets before PostMount: {provider.Files.Keys.Count(x =>
            x.Replace('\\', '/').Contains("/NewDisplayAssets/", StringComparison.OrdinalIgnoreCase)):N0}");

        provider.LoadVirtualPaths();
        provider.PostMount();

        var finalFileCount = provider.Files.Count;
        var newDisplayAssetCount = provider.Files.Keys.Count(x =>
            x.Replace('\\', '/').Contains("/NewDisplayAssets/", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"Provider files after PostMount: {finalFileCount:N0}");
        Console.WriteLine($"NewDisplayAssets after PostMount: {newDisplayAssetCount:N0}");

        // Diagnostics: keep a complete list of every mounted NewDisplayAssets path.
        var displayAssetPaths = provider.Files.Keys
            .Where(x => x.Replace('\\', '/').Contains("/NewDisplayAssets/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await File.WriteAllLinesAsync(Path.Combine(DataDir, "new-display-assets.txt"), displayAssetPaths);

        var dAv2Paths = displayAssetPaths
            .Where(IsCloudDisplayAssetVirtualPath)
            .ToArray();
        await File.WriteAllLinesAsync(Path.Combine(DataDir, "new-display-assets-dav2.txt"), dAv2Paths);

        var iadManifestFiles = manifest.Files.Select(x => x.FileName)
            .Where(x => x.Contains("iad", StringComparison.OrdinalIgnoreCase) ||
                        x.Contains("ondemand", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await File.WriteAllLinesAsync(Path.Combine(DataDir, "manifest-iad-files.txt"), iadManifestFiles);

        var offerCatalogFiles = provider.Files.Keys
            .Where(x => x.Contains("OfferCatalog", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await File.WriteAllLinesAsync(Path.Combine(DataDir, "provider-offercatalog-files.txt"), offerCatalogFiles);

        await File.WriteAllTextAsync(Path.Combine(DataDir, "provider-stats.txt"),
            $"Live manifest files: {manifest.Files.Count}\n" +
            $"Live manifest size: {manifestInfoBytes.Length}\n" +
            $"Live manifest SHA256: {Convert.ToHexString(SHA256.HashData(manifestInfoBytes))}\n" +
            $"Cloud Manifest URL: {LastCloudManifestUrl}\n" +
            $"Cloud Manifest size: {LastCloudManifestSize}\n" +
            $"Cloud Manifest SHA256: {LastCloudManifestSha256}\n" +
            $"Cloud archives registered: {cloudArchiveCount}\n" +
            $"Mounted archives: {mountedArchives}\n" +
            $"Provider files after mount: {totalFilesAfterMount}\n" +
            $"Provider files after PostMount: {finalFileCount}\n" +
            $"NewDisplayAssets: {newDisplayAssetCount}\n" +
            $"DAv2 uasset files: {dAv2Paths.Length}\n" +
            $"OfferCatalog files: {offerCatalogFiles.Length}\n" +
            $"Embedded .uondemandtoc count: {embeddedTocs.Length}\n" +
            $"IoStoreOnDemand.ini: {(iniFile is null ? "missing" : "found")}\n" +
            $"TocPath: {liveTocPath ?? "missing"}\n");

        Console.WriteLine($"الفهرس المركب النهائي: {finalFileCount:N0} ملف، NewDisplayAssets={newDisplayAssetCount:N0}، DAv2={dAv2Paths.Length:N0}، OfferCatalog={offerCatalogFiles.Length:N0}.");

        var mappingsPath = Path.Combine(DataDir, "mappings.usmap");
        await File.WriteAllBytesAsync(mappingsPath, await Http.GetByteArrayAsync(version.Mappings.Url));
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingsPath, StringComparer.Ordinal);
        return provider;
#endif
    }

    private static async Task<(int Registered, int Mounted, string? ManifestUrl)> MountLocalCloudArchivesAsync(
        HybridFileProvider provider,
        VersionInfo version,
        string chunksDirectory,
        string manifestDirectory)
    {
        var cloudContentPath = FindLocalCloudContentPath();
        if (cloudContentPath is null)
        {
            Console.WriteLine(
                "تحذير: لم يوجد Fortnite/Cloud/cloudcontent.json؛ لن تُركب طبقة Cloud المحلية.");
            return (0, 0, null);
        }

        Console.WriteLine($"Cloud content: {cloudContentPath}");
        var cloudContent = JsonConvert.DeserializeObject<CloudContentInfo>(
            await File.ReadAllTextAsync(cloudContentPath));
        if (cloudContent is null || string.IsNullOrWhiteSpace(cloudContent.ManifestPath))
        {
            Console.WriteLine("تحذير: cloudcontent.json لا يحتوي ManifestPath صالحًا.");
            return (0, 0, null);
        }

        var manifestUrl = cloudContent.ManifestPath.StartsWith(
            "http", StringComparison.OrdinalIgnoreCase)
            ? cloudContent.ManifestPath
            : "https://egdownload.fastly-edge.com/" + cloudContent.ManifestPath.TrimStart('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        using var response = await Http.SendAsync(request);
        var manifestBytes = await response.Content.ReadAsByteArrayAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"فشل تنزيل Fortnite Cloud Manifest: {(int)response.StatusCode} {response.ReasonPhrase}");

        var manifestUri = new Uri(manifestUrl);
        var cloudOptions = new ManifestParseOptions
        {
            ChunkBaseUrl = new Uri(manifestUri, ".").AbsoluteUri,
            ChunkCacheDirectory = chunksDirectory,
            ManifestCacheDirectory = manifestDirectory,
            CacheChunksAsIs = false
        };
        var cloudManifest = FBuildPatchAppManifest.Deserialize(manifestBytes, cloudOptions);
        var filesBefore = provider.Files.Count;
        var registered = await provider.RegisterManifestAsync(cloudManifest);
        var mounted = await provider.MountAsync();
        await SubmitFortniteKeysAsync(provider, version);
        Console.WriteLine(
            $"تم تسجيل {registered} وتركيب {mounted} حاوية Cloud؛ " +
            $"أضيف {provider.Files.Count - filesBefore:N0} ملف إلى provider.");
        return (registered, mounted, manifestUrl);
    }

    private static string? FindLocalCloudContentPath()
    {
        var candidates = new List<string>();
        var customInstall = Environment.GetEnvironmentVariable("FORTNITE_INSTALL_DIR");
        if (!string.IsNullOrWhiteSpace(customInstall))
            candidates.Add(Path.Combine(customInstall.Trim('"'), "Cloud", "cloudcontent.json"));

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Epic Games", "Fortnite", "Cloud", "cloudcontent.json"));
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            candidates.Add(Path.Combine(programFilesX86,
                "Epic Games", "Fortnite", "Cloud", "cloudcontent.json"));

        var launcherManifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(launcherManifests))
        {
            foreach (var itemPath in Directory.EnumerateFiles(launcherManifests, "*.item"))
            {
                try
                {
                    var item = JsonConvert.DeserializeObject<EpicInstallItem>(File.ReadAllText(itemPath));
                    if (item is null || string.IsNullOrWhiteSpace(item.InstallLocation)) continue;
                    if (!item.AppName.Equals("Fortnite", StringComparison.OrdinalIgnoreCase) &&
                        !item.DisplayName.Contains("Fortnite", StringComparison.OrdinalIgnoreCase)) continue;
                    candidates.Add(Path.Combine(
                        item.InstallLocation, "Cloud", "cloudcontent.json"));
                }
                catch { }
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static string? ParseOnDemandIniValue(string ini, string wantedKey)
    {
        foreach (var rawLine in ini.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0) continue;
            var key = line[..equalsIndex].Trim();
            if (!key.Equals(wantedKey, StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(equalsIndex + 1)..].Trim().Trim('"', '\'');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }

    private static async Task<int> RegisterOnDemandTocsAsync(
        HybridFileProvider provider,
        FBuildPatchAppManifest manifest,
        string sourceName)
    {
        var tocFiles = manifest.Files
            .Where(x => x.FileName.EndsWith(
                ".uondemandtoc", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var toc in tocFiles)
        {
            // V2 TOCs span many BuildPatch chunks. Fully materialize them first,
            // matching FModel, instead of making the synchronous TOC parser fetch
            // those chunks serially.
            using var input = toc.GetStream();
            using var memory = new MemoryStream();
            await input.CopyToAsync(memory);
            await provider.RegisterOnDemandAsync(memory.ToArray(), Path.GetFileName(toc.FileName), sourceName);
            Console.WriteLine($"تم تسجيل {sourceName} TOC: {toc.FileName}");
        }

        return tocFiles.Length;
    }

    private static async Task SubmitFortniteKeysAsync(HybridFileProvider provider, VersionInfo version)
    {
        await provider.SubmitKeyAsync(new CUE4Parse.UE4.Objects.Core.Misc.FGuid(),
            new FAesKey(version.Keys.MainKey.Key));
        foreach (var key in version.Keys.ExtraKeys)
            await provider.SubmitKeyAsync(new CUE4Parse.UE4.Objects.Core.Misc.FGuid(key.Guid), new FAesKey(key.Key));
    }

    private static async Task<CatalogSnapshotState?> LoadCatalogSnapshotAsync()
    {
        if (!File.Exists(SnapshotFile)) return null;
        try
        {
            var snapshot = JsonConvert.DeserializeObject<CatalogSnapshotState>(await File.ReadAllTextAsync(SnapshotFile));
            return snapshot is null
                ? null
                : snapshot with { Offers = new(snapshot.Offers, StringComparer.OrdinalIgnoreCase) };
        }
        catch
        {
            Console.WriteLine("تعذر قراءة Catalog Snapshot؛ سيُعاد بناؤه بصمت.");
            return null;
        }
    }

    private static async Task<HashSet<string>?> LoadSeenDisplayAssetsAsync()
    {
        try
        {
            if (File.Exists(SeenDisplayAssetsFile))
            {
                var state = JsonConvert.DeserializeObject<DisplayAssetCacheState>(
                    await File.ReadAllTextAsync(SeenDisplayAssetsFile));
                if (state is not null)
                    return new HashSet<string>(state.SeenPaths.Where(x => !string.IsNullOrWhiteSpace(x)),
                        StringComparer.OrdinalIgnoreCase);
            }

            // One-time migration from older releases that cached records by OfferId.
            var oldSnapshot = await LoadCatalogSnapshotAsync();
            if (oldSnapshot is not null)
            {
                var migrated = oldSnapshot.Offers.Values
                    .Select(x => x.AssetKey)
                    .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("grants:", StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                await SaveSeenDisplayAssetsAsync(migrated);
                Console.WriteLine($"تم تحويل الكاش القديم إلى {migrated.Count} مسار NewDisplayAssetPath.");
                return migrated;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"تعذر قراءة كاش NewDisplayAssetPath: {e.Message}");
        }
        return null;
    }

    private static async Task<HashSet<string>?> LoadSeenCatalogOffersAsync()
    {
        try
        {
            if (File.Exists(SeenCatalogOffersFile))
            {
                var state = JsonConvert.DeserializeObject<CatalogOfferCacheState>(
                    await File.ReadAllTextAsync(SeenCatalogOffersFile));
                if (state is not null)
                    return new HashSet<string>(state.SeenOfferKeys.Where(x => !string.IsNullOrWhiteSpace(x)),
                        StringComparer.OrdinalIgnoreCase);
            }

            // Releases before v47 stored offer records in catalog-snapshot.json.
            var snapshot = await LoadCatalogSnapshotAsync();
            if (snapshot is not null && snapshot.Offers.Count > 0)
            {
                var migrated = snapshot.Offers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                await SaveSeenCatalogOffersAsync(migrated);
                Console.WriteLine($"تم تحويل Catalog Snapshot القديم إلى {migrated.Count} عرض محفوظ.");
                return migrated;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"تعذر قراءة كاش عروض Catalog: {e.Message}");
        }
        return null;
    }

    private static async Task SaveSeenCatalogOffersAsync(HashSet<string> offerKeys)
    {
        var state = new CatalogOfferCacheState(DateTimeOffset.UtcNow,
            new HashSet<string>(offerKeys, StringComparer.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(SeenCatalogOffersFile,
            JsonConvert.SerializeObject(state, Formatting.Indented));
    }

    private static async Task<Dictionary<string, string>?> LoadCloudDisplayAssetCacheAsync()
    {
        if (!File.Exists(CloudDisplayAssetCacheFile)) return null;
        try
        {
            var state = JsonConvert.DeserializeObject<CloudDisplayAssetCacheState>(
                await File.ReadAllTextAsync(CloudDisplayAssetCacheFile));
            if (state is not null && state.SignatureVersion != CloudSignatureVersion)
            {
                CloudCacheNeedsSignatureUpgrade = true;
                Console.WriteLine($"كاش Cloud يستخدم توقيعًا قديمًا (v{state.SignatureVersion})؛ ستتم ترقيته دون إرسال القديم.");
            }
            return state is null
                ? null
                : new Dictionary<string, string>(state.Files, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            Console.WriteLine($"تعذر قراءة كاش Cloud Display Assets: {e.Message}");
            return null;
        }
    }

    private static async Task SaveCloudDisplayAssetCacheAsync(Dictionary<string, string> files)
    {
        var state = new CloudDisplayAssetCacheState(DateTimeOffset.UtcNow,
            new Dictionary<string, string>(files, StringComparer.OrdinalIgnoreCase), CloudSignatureVersion);
        await File.WriteAllTextAsync(CloudDisplayAssetCacheFile,
            JsonConvert.SerializeObject(state, Formatting.Indented));
    }

    private static async Task<HashSet<string>> LoadSeenRenderImageHashesAsync()
    {
        if (!File.Exists(SeenRenderImageHashesFile))
            return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var state = JsonConvert.DeserializeObject<DisplayAssetCacheState>(
                await File.ReadAllTextAsync(SeenRenderImageHashesFile));
            return state is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(state.SeenPaths, StringComparer.Ordinal);
        }
        catch (Exception error)
        {
            Console.WriteLine($"تعذر قراءة كاش بصمات الصور: {error.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static async Task SaveSeenRenderImageHashesAsync(HashSet<string> hashes)
    {
        var state = new DisplayAssetCacheState(DateTimeOffset.UtcNow,
            new HashSet<string>(hashes, StringComparer.Ordinal));
        await File.WriteAllTextAsync(SeenRenderImageHashesFile,
            JsonConvert.SerializeObject(state, Formatting.Indented));
    }

    private static async Task<string> CalculateFileSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static Dictionary<string, string> BuildInitialCloudDisplayAssetCache(
        Dictionary<string, string> current, HashSet<string> previousPaths)
    {
        if (previousPaths.Count == 0) return new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);

        var baseline = current
            .Where(pair => previousPaths.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"تم استخدام فهرس OfferCatalog السابق كأساس ({baseline.Count} ملف). الملفات المضافة بعده ستُستخرج الآن.");
        return baseline;
    }

    private static async Task<HashSet<string>> LoadLegacyCloudDisplayAssetPathsAsync()
    {
        var legacyIndex = Path.Combine(DataDir, "provider-offercatalog-files.txt");
        if (!File.Exists(legacyIndex)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (await File.ReadAllLinesAsync(legacyIndex))
            .Where(IsCloudDisplayAssetVirtualPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildCloudDisplayAssetIndex(HybridFileProvider provider)
    {
        var candidates = provider.Files
            .Where(pair => IsCloudDisplayAssetVirtualPath(pair.Key))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => GetMountedFileSignature(group.Last().Value),
                StringComparer.OrdinalIgnoreCase);
        var newDisplayAssetPaths = provider.Files.Keys.Count(path =>
            path.Replace('\\', '/').Contains("/NewDisplayAssets/", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"فهرس NewDisplayAssets: {newDisplayAssetPaths} مسار، منها {candidates.Count} ملف uasset قابل للتعقب.");
        return candidates;
    }

    private static bool IsCloudDisplayAssetVirtualPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var fileName = Path.GetFileNameWithoutExtension(normalized);
        // Cloud containers can expose different virtual mount roots. The stable
        // identity is the complete NewDisplayAssets virtual package path.
        return normalized.Contains("/NewDisplayAssets/", StringComparison.OrdinalIgnoreCase) &&
               Path.GetExtension(normalized).Equals(".uasset", StringComparison.OrdinalIgnoreCase) &&
               fileName.StartsWith("DAv2", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMountedFileSignature(object file)
    {
        // VFS entries keep the useful IoStore/BuildPatch identity in nested objects
        // (chunk ids, content hashes, offsets and sizes). Hashing only the direct
        // primitive properties misses some Cloud replacements whose virtual path is
        // unchanged. Build a bounded, deterministic identity from those nested fields.
        var values = new List<string>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        AppendMountedIdentity(file, file.GetType().Name, 0, values, visited);
        var raw = string.Join("|", values.OrderBy(value => value, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static void AppendMountedIdentity(
        object? value, string path, int depth, List<string> values, HashSet<object> visited)
    {
        if (value is null || depth > 4) return;
        var type = value.GetType();
        if (value is string or Guid or decimal || type.IsPrimitive || type.IsEnum)
        {
            values.Add($"{path}={value}");
            return;
        }
        if (value is byte[] bytes)
        {
            values.Add($"{path}=bytes:{Convert.ToHexString(SHA256.HashData(bytes))}");
            return;
        }
        if (!type.IsValueType && !visited.Add(value)) return;

        if (value is System.Collections.IEnumerable enumerable)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                if (index >= 128) break;
                AppendMountedIdentity(item, $"{path}[{index}]", depth + 1, values, visited);
                index++;
            }
            values.Add($"{path}.Count={index}");
            return;
        }

        foreach (var property in type.GetProperties()
                     .Where(property => property.GetIndexParameters().Length == 0 && property.CanRead)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            // Avoid traversing provider/container graphs. Keep properties that can
            // identify bytes or their location in an IoStore/manifest.
            var name = property.Name;
            if (depth > 0 && !IdentityPropertyName(name)) continue;
            try
            {
                AppendMountedIdentity(property.GetValue(value), $"{path}.{name}", depth + 1, values, visited);
            }
            catch { }
        }
    }

    private static bool IdentityPropertyName(string name) =>
        name.Contains("hash", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("chunk", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("guid", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("offset", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("size", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("length", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("id", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("compression", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("entry", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("toc", StringComparison.OrdinalIgnoreCase);

    private static string ShortFingerprint(string value) =>
        value.Length <= 16 ? value : value[..16];

    private static CatalogEntry CreateCloudCatalogEntry(string virtualFilePath)
    {
        var objectPath = CloudObjectPathFromVirtualFile(virtualFilePath);
        var name = GetDisplayAssetName(objectPath);
        return new CatalogEntry(
            $"cloud:{objectPath}",
            name,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NewDisplayAssetPath"] = objectPath
            },
            []);
    }

    private static string CloudObjectPathFromVirtualFile(string virtualFilePath)
    {
        var normalized = virtualFilePath.Replace('\\', '/').TrimStart('/');
        var marker = normalized.IndexOf("NewDisplayAssets/", StringComparison.OrdinalIgnoreCase);
        var package = marker >= 0
            ? "/" + normalized[marker..]
            : "/" + normalized;

        var extensionIndex = package.LastIndexOf('.');
        if (extensionIndex > package.LastIndexOf('/'))
            package = package[..extensionIndex];

        var name = Path.GetFileName(package);
        return $"{package}.{name}";
    }

    private static async Task SaveSeenDisplayAssetsAsync(HashSet<string> paths)
    {
        var state = new DisplayAssetCacheState(DateTimeOffset.UtcNow,
            new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(SeenDisplayAssetsFile,
            JsonConvert.SerializeObject(state, Formatting.Indented));
    }

    private static async Task<HashSet<string>?> LoadSeenDav2PathsAsync()
    {
        if (!File.Exists(SeenDav2PathsFile)) return null;
        try
        {
            var state = JsonConvert.DeserializeObject<DisplayAssetCacheState>(
                await File.ReadAllTextAsync(SeenDav2PathsFile));
            return state is null
                ? null
                : new HashSet<string>(
                    state.SeenPaths.Where(path => !string.IsNullOrWhiteSpace(path)),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error)
        {
            Console.WriteLine($"تعذر قراءة كاش DAv2: {error.Message}");
            return null;
        }
    }

    private static async Task SaveSeenDav2PathsAsync(HashSet<string> paths)
    {
        var state = new DisplayAssetCacheState(DateTimeOffset.UtcNow,
            new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(SeenDav2PathsFile,
            JsonConvert.SerializeObject(state, Formatting.Indented));
    }

    private static async Task SaveCurrentDisplayAssetIndexAsync(
        IReadOnlyList<(string Storefront, CatalogEntry Entry)> candidates)
    {
        var index = candidates.Select(x => new
        {
            storefront = x.Storefront,
            offerId = x.Entry.OfferId,
            devName = x.Entry.DevName,
            newDisplayAssetPath = GetRawDisplayAssetPath(x.Entry),
            trackedCosmetic = HasTrackedCosmeticGrant(x.Entry),
            itemTypes = (x.Entry.ItemGrants ?? [])
                .Select(grant => grant.TemplateId.Split(':')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        }).OrderBy(x => x.newDisplayAssetPath, StringComparer.OrdinalIgnoreCase).ToArray();

        await File.WriteAllTextAsync(Path.Combine(DataDir, "catalog-display-assets.json"),
            JsonConvert.SerializeObject(index, Formatting.Indented));
    }

    private static string GetAssetKey(CatalogEntry entry)
    {
        return TryGetDisplayAssetPath(entry, out var path) ? path.ToLowerInvariant() : "";
    }

    private static string GetOfferKey(string storefront, CatalogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.OfferId)) return entry.OfferId.Trim().ToLowerInvariant();
        var grants = string.Join("|", (entry.ItemGrants ?? [])
            .Select(grant => $"{grant.TemplateId}:{grant.Quantity}")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        return $"{storefront}|{entry.DevName}|{GetAssetKey(entry)}|{grants}".ToLowerInvariant();
    }

    private static bool HasTrackedCosmeticGrant(CatalogEntry entry)
    {
        return (entry.ItemGrants ?? []).Any(grant => IsTrackedCosmeticType(
            grant.TemplateId.Split(':')[0]));
    }

    private static bool IsTrackedCosmeticType(string type) =>
        TrackedCosmeticTypes.Contains(type) ||
        type.StartsWith("Sparks", StringComparison.OrdinalIgnoreCase);

    private static (int Priority, string Type) GetOfferSortKey(CatalogEntry entry)
    {
        var relevant = (entry.ItemGrants ?? [])
            .Where(grant => IsTrackedCosmeticType(grant.TemplateId.Split(':')[0]))
            .ToArray();
        var isBundle = relevant.Length > 1 || entry.DevName.Contains("bundle", StringComparison.OrdinalIgnoreCase);
        var containsOutfit = relevant.Any(x =>
            x.TemplateId.StartsWith("AthenaCharacter:", StringComparison.OrdinalIgnoreCase));
        if (isBundle && containsOutfit) return (0, "SkinBundle");
        if (isBundle)
        {
            var bundleType = relevant.FirstOrDefault()?.TemplateId.Split(':')[0] ?? "OtherBundle";
            return (1, bundleType);
        }

        var type = relevant.FirstOrDefault()?.TemplateId.Split(':')[0] ?? "Other";
        var priority = type.ToLowerInvariant() switch
        {
            "athenacharacter" => 2,
            "athenabackpack" => 3,
            "athenapickaxe" => 4,
            "athenadance" => 5,
            "athenaitemwrap" => 6,
            "athenaglider" => 7,
            "athenaskydivecontrail" => 8,
            "cosmeticshoes" => 9,
            "sparks" => 10,
            "cosmeticmimosa" => 11,
            _ => 12
        };
        return (priority, type);
    }

    private static IEnumerable<(string Storefront, CatalogEntry Entry)> SelectBalancedRandomCandidates(
        IReadOnlyList<(string Storefront, CatalogEntry Entry)> candidates)
    {
        // Take one random entry from every detected type first, then fill the rest
        // randomly. Publishing still applies the final stable type order.
        var shuffledGroups = candidates
            .GroupBy(x => GetOfferSortKey(x.Entry).Type, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Queue<(string Storefront, CatalogEntry Entry)>(
                group.OrderBy(_ => Random.Shared.Next())))
            .OrderBy(_ => Random.Shared.Next())
            .ToList();

        while (shuffledGroups.Count > 0)
        {
            foreach (var group in shuffledGroups.ToArray())
            {
                if (group.Count > 0) yield return group.Dequeue();
                if (group.Count == 0) shuffledGroups.Remove(group);
            }
        }
    }

    private static bool TryGetDisplayAssetPath(CatalogEntry entry, out string path)
    {
        path = "";
        var rawPath = GetRawDisplayAssetPath(entry);
        if (string.IsNullOrWhiteSpace(rawPath)) return false;

        path = rawPath.Split('.')[0].Trim().TrimStart('/').Replace('\\', '/');
        return !string.IsNullOrWhiteSpace(path) &&
               !path.StartsWith("sid_placeholder", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetRawDisplayAssetPath(CatalogEntry entry)
    {
        var metaValue = entry.Meta?.FirstOrDefault(
            x => x.Key.Equals("NewDisplayAssetPath", StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrWhiteSpace(metaValue)) return metaValue;

        if (entry.AdditionalData is null) return null;
        var directValue = entry.AdditionalData
            .FirstOrDefault(x => x.Key.Equals("NewDisplayAssetPath", StringComparison.OrdinalIgnoreCase))
            .Value?.Value<string>();
        if (!string.IsNullOrWhiteSpace(directValue)) return directValue;

        var metaInfo = entry.AdditionalData
            .FirstOrDefault(x => x.Key.Equals("metaInfo", StringComparison.OrdinalIgnoreCase)).Value as JArray;
        if (metaInfo is null) return null;
        foreach (var item in metaInfo.OfType<JObject>())
        {
            var key = item.Properties()
                .FirstOrDefault(x => x.Name.Equals("key", StringComparison.OrdinalIgnoreCase))?.Value.Value<string>();
            if (!string.Equals(key, "NewDisplayAssetPath", StringComparison.OrdinalIgnoreCase)) continue;
            var value = item.Properties()
                .FirstOrDefault(x => x.Name.Equals("value", StringComparison.OrdinalIgnoreCase))?.Value.Value<string>();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string GetDisplayAssetName(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return "unknown_display_asset";
        var packagePath = rawPath.Split('.')[0].Replace('\\', '/');
        return Sanitize(Path.GetFileName(packagePath));
    }

    private static async Task<ExtractedOfferArtifact?> ExtractOfferAsync(
        HybridFileProvider provider, CatalogEntry entry, string storefront, string changeType,
        string? requiredRenderSeason = null)
    {
        var rawPath = GetRawDisplayAssetPath(entry);
        TryGetDisplayAssetPath(entry, out var path);
        var displayAssetName = GetDisplayAssetName(rawPath);
        var folder = Path.Combine(OutputDir, Sanitize(entry.OfferId));
        Directory.CreateDirectory(folder);
        ClearPreviousGeneratedFiles(folder);
        var exported = new List<string>();
        string? primaryImagePath = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                // Do not resolve this through the IoStore package-id index. Fortnite can have
                // the same DisplayAsset in the base archives and in a newer Cloud archive, and
                // CUE4Parse's package-id index may return the older copy. Resolve the mounted
                // file by virtual path and load that exact file so Cloud read order wins.
                var asset = await LoadDisplayAssetByMountedPathAsync(provider, path, displayAssetName);
                try
                {
                    var debugSettings = new JsonSerializerSettings
                    {
                        Formatting = Formatting.Indented,
                        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                        MaxDepth = 8
                    };
                    await File.WriteAllTextAsync(Path.Combine(folder, "display-asset-debug.json"),
                        JsonConvert.SerializeObject(asset.ContextualPresentations, debugSettings));
                }
                catch (Exception debugError)
                {
                    await File.WriteAllTextAsync(Path.Combine(folder, "display-asset-debug-error.txt"), debugError.ToString());
                }

                var presentations = asset.ContextualPresentations
                    .Select((value, index) => (Value: value, Index: index))
                    .Where(x => requiredRenderSeason is null ||
                                IsPresentationFromSeason(x.Value, requiredRenderSeason))
                    .OrderByDescending(x => x.Value.ProductTag.TagName.Text.Equals(
                        "Product.BR", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (requiredRenderSeason is not null && presentations.Length == 0)
                {
                    Console.WriteLine($"DisplayAsset {displayAssetName}: لا يحتوي Render Image من {requiredRenderSeason}.");
                    return null;
                }
                foreach (var presentation in presentations)
                {
                    try
                    {
                        var renderPath = presentation.Value.RenderImage.AssetPathName.Text;
                        var materialPath = presentation.Value.OverrideImageMaterial.AssetPathName.Text;
                        if (IsKnownPlaceholderRenderPath(renderPath) && string.IsNullOrWhiteSpace(materialPath))
                        {
                            await File.WriteAllTextAsync(
                                Path.Combine(folder, $"presentation_{presentation.Index}_placeholder.txt"),
                                renderPath);
                            continue;
                        }

                        var name = $"{displayAssetName}.png";
                        var outputPath = Path.Combine(folder, name);
                        var imageSource = await TrySavePresentationImageAsync(
                            provider, presentation.Value, outputPath);
                        if (imageSource is not null)
                        {
                            exported.Add(name);
                            primaryImagePath = outputPath;
                            Console.WriteLine(
                                $"DisplayAsset {displayAssetName}: presentation {presentation.Index} rendered from {imageSource}");
                            break;
                        }

                        await File.WriteAllTextAsync(
                            Path.Combine(folder, $"presentation_{presentation.Index}_no-image.txt"),
                            $"ProductTag: {presentation.Value.ProductTag.TagName.Text}\n" +
                            $"RenderImage: {renderPath}\n" +
                            $"OverrideImageMaterial: {materialPath}\n" +
                            $"PoseTestGroup: {presentation.Value.PoseTestGroup}\n");
                    }
                    catch (Exception presentationError)
                    {
                        await File.WriteAllTextAsync(
                            Path.Combine(folder, $"presentation_{presentation.Index}_error.txt"),
                            presentationError.ToString());
                    }
                }
            }
            catch (Exception e) { await File.WriteAllTextAsync(Path.Combine(folder,"error.txt"), e.ToString()); }
        }

        var result = new ExtractedOffer(entry.OfferId, entry.DevName, rawPath,
            entry.ItemGrants?.Select(x=>x.TemplateId).ToArray() ?? [], exported.ToArray(), DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(folder,"offer.json"), JsonConvert.SerializeObject(result,Formatting.Indented));
        Console.WriteLine($"عرض جديد: {entry.DevName} | الصور: {exported.Count}");
        return primaryImagePath is null
            ? null
            : new ExtractedOfferArtifact(entry, storefront, displayAssetName, primaryImagePath, changeType);
    }

    private static bool IsPresentationFromSeason(FContextualPresentation presentation, string season)
    {
        var segment = $"/{season.Trim('/')}/";
        return presentation.RenderImage.AssetPathName.Text.Replace('\\', '/').Contains(
                   segment, StringComparison.OrdinalIgnoreCase) ||
               presentation.OverrideImageMaterial.AssetPathName.Text.Replace('\\', '/').Contains(
                   segment, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> TrySavePresentationImageAsync(
        HybridFileProvider provider,
        FContextualPresentation presentation,
        string outputPath)
    {
        var renderPath = presentation.RenderImage.AssetPathName.Text;
        if (!string.IsNullOrWhiteSpace(renderPath) && !IsKnownPlaceholderRenderPath(renderPath))
        {
            try
            {
                var texture = await TryLoadObjectByMountedPathAsync<UTexture2D>(provider, renderPath)
                              ?? await presentation.RenderImage.TryLoadAsync<UTexture2D>(provider);
                if (texture is not null && await SaveTextureAsync(texture, outputPath))
                    return $"RenderImage ({renderPath})";
            }
            catch (KeyNotFoundException)
            {
                // Some shop presentations reference textures that are not present in
                // the mounted archives. Continue to OverrideImageMaterial as fallback.
                Console.WriteLine($"Presentation RenderImage is not mounted: {renderPath}");
            }
        }

        var materialPath = presentation.OverrideImageMaterial.AssetPathName.Text;
        if (string.IsNullOrWhiteSpace(materialPath)) return null;

        UMaterialInterface? material;
        try
        {
            material = await TryLoadObjectByMountedPathAsync<UMaterialInterface>(provider, materialPath)
                       ?? await presentation.OverrideImageMaterial.TryLoadAsync<UMaterialInterface>(provider);
        }
        catch (KeyNotFoundException)
        {
            Console.WriteLine($"Presentation OverrideImageMaterial is not mounted: {materialPath}");
            return null;
        }
        if (material is null) return null;

        var parameters = new CMaterialParams();
        material.GetParams(parameters);
        var materialTextures = new[]
            {
                (Name: "Diffuse", Texture: parameters.Diffuse),
                (Name: "Emissive", Texture: parameters.Emissive),
                (Name: "Opacity", Texture: parameters.Opacity),
                (Name: "Misc", Texture: parameters.Misc)
            }
            .Where(candidate => candidate.Texture is UTexture2D)
            .Select(candidate => (candidate.Name, Texture: (UTexture2D) candidate.Texture!));

        foreach (var candidate in materialTextures)
        {
            if (await SaveTextureAsync(candidate.Texture, outputPath))
                return $"OverrideImageMaterial.{candidate.Name} ({materialPath})";
        }

        return null;
    }

    private static async Task PublishCatalogUpdateAsync(List<ExtractedOfferArtifact> artifacts, bool isTest)
    {
        // The composite and local JSON use the same stable grouping:
        // bundles, outfits, backpacks, pickaxes, emotes, wraps, then every other type.
        artifacts = artifacts
            .OrderBy(x => GetOfferSortKey(x.Entry).Priority)
            .ThenBy(x => GetOfferSortKey(x.Entry).Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DisplayAssetName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        // Keep the lossless PNG output at an exact 512x512 tile per cosmetic.
        var compositePath = Path.Combine(UpdatesDir, $"shop-update_{timestamp}.png");
        CreateCompositeImage(artifacts.Select(x => x.ImagePath).ToArray(), compositePath);
        using (var verifiedComposite = SKBitmap.Decode(compositePath))
        {
            if (verifiedComposite is null || verifiedComposite.Width == 0 || verifiedComposite.Height == 0)
                throw new InvalidDataException($"فشل التحقق من الصورة المركبة: {compositePath}");
            Console.WriteLine(
                $"تم التحقق من الصورة المركبة: {artifacts.Count} عنصر، {verifiedComposite.Width}x{verifiedComposite.Height}.");
        }

        var compositeUrl = "";
        var webhook = await GetDiscordWebhookAsync();
        if (webhook is null && !isTest)
            throw new Exception("لم يتم ضبط Discord Webhook؛ لن يُحفظ التغيير كي تتم إعادة المحاولة.");
        if (webhook is not null)
            compositeUrl = await UploadCompositeEmbedAsync(webhook, compositePath)
                ?? throw new Exception("لم يؤكد Discord استلام الصورة المركبة؛ ستتم إعادة المحاولة.");
        else
            Console.WriteLine("وضع الاختبار: تم حفظ الصورة المركبة محليًا دون إرسال Discord.");

        var items = artifacts.Select(x => new PublishedCatalogItem(
            x.Entry.OfferId,
            x.Entry.DevName,
            x.Storefront,
            x.DisplayAssetName,
            GetRawDisplayAssetPath(x.Entry) ?? "",
            Path.GetFileName(x.ImagePath),
            Path.GetRelativePath(UpdatesDir, x.ImagePath),
            x.ChangeType,
            x.Entry.ItemGrants ?? [],
            x.Entry.Meta,
            x.Entry.AdditionalData)).ToArray();

        var update = new CatalogUpdateFile(DateTimeOffset.UtcNow, items.Length,
            Path.GetFileName(compositePath), compositeUrl, items);
        var jsonPath = Path.Combine(UpdatesDir, $"shop-update_{timestamp}.json");
        await File.WriteAllTextAsync(jsonPath, JsonConvert.SerializeObject(update, Formatting.Indented));

        Console.WriteLine($"تم إنشاء تحديث مجمع يضم {items.Length} عنصرًا: {compositePath}");
    }

    private static async Task<Uri?> GetDiscordWebhookAsync()
    {
        if (!File.Exists(WebhookFile)) return null;
        var value = (await File.ReadAllTextAsync(WebhookFile)).Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !(uri.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith("discordapp.com", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("رابط Discord Webhook غير صالح؛ سيتم حفظ التحديث محليًا فقط.");
            return null;
        }

        var builder = new UriBuilder(uri);
        var query = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrWhiteSpace(query) ? "wait=true" : query + "&wait=true";
        return builder.Uri;
    }

    private static async Task<Dictionary<string, string>> UploadFilesToDiscordAsync(
        Uri webhook, IReadOnlyList<string> files, string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sentContent = false;
        foreach (var indexedBatch in files.Select((path, index) => (path, index)).Chunk(10))
        {
            using var form = new MultipartFormDataContent();
            var payload = JsonConvert.SerializeObject(new
            {
                content = sentContent ? "" : content,
                flags = 4096,
                allowed_mentions = new { parse = Array.Empty<string>() }
            });
            sentContent = true;
            form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
            var streams = new List<Stream>();
            try
            {
                for (var i = 0; i < indexedBatch.Length; i++)
                {
                    var path = indexedBatch[i].path;
                    var stream = File.OpenRead(path);
                    streams.Add(stream);
                    var fileContent = new StreamContent(stream);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue(
                        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
                            ? "application/json" : "image/png");
                    form.Add(fileContent, $"files[{i}]", Path.GetFileName(path));
                }

                var response = await Http.PostAsync(webhook, form);
                var responseText = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"تعذر إرسال الملفات إلى Discord: {(int)response.StatusCode} {responseText}");
                    continue;
                }

                var attachments = JObject.Parse(responseText)["attachments"] as JArray;
                if (attachments is null) continue;
                for (var i = 0; i < Math.Min(indexedBatch.Length, attachments.Count); i++)
                {
                    var url = attachments[i]?["url"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(url)) result[indexedBatch[i].path] = url;
                }
            }
            finally
            {
                foreach (var stream in streams) await stream.DisposeAsync();
            }
        }
        return result;
    }

    private static async Task<string?> UploadCompositeEmbedAsync(Uri webhook, string imagePath)
    {
        using var form = new MultipartFormDataContent();
        var fileName = Path.GetFileName(imagePath);
        var payload = JsonConvert.SerializeObject(new
        {
            content = "🔔 New Streamed Assets Added!",
            embeds = new[]
            {
                new
                {
                    color = 0x2DB7F5,
                    image = new { url = $"attachment://{fileName}" },
                    footer = new { text = "Made by @ViberLeaks" },
                    timestamp = DateTimeOffset.UtcNow.ToString("O")
                }
            },
            allowed_mentions = new { parse = Array.Empty<string>() }
        });
        form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
        await using var stream = File.OpenRead(imagePath);
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "files[0]", fileName);

        var response = await Http.PostAsync(webhook, form);
        var responseText = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"تعذر إرسال الصورة المجمعة إلى Discord: {(int)response.StatusCode} {responseText}");
            await File.WriteAllTextAsync(Path.Combine(DataDir, "discord-upload-error.txt"),
                $"HTTP {(int)response.StatusCode} {response.StatusCode}\n{responseText}");
            return null;
        }

        var responseJson = JObject.Parse(responseText);
        var firstAttachment = (responseJson["attachments"] as JArray)?.FirstOrDefault();
        var firstEmbed = (responseJson["embeds"] as JArray)?.FirstOrDefault();
        var attachmentUrl = firstAttachment?["url"]?.Value<string>();
        var embedImageUrl = firstEmbed?["image"]?["url"]?.Value<string>();
        var confirmedUrl = !string.IsNullOrWhiteSpace(attachmentUrl) ? attachmentUrl : embedImageUrl;
        if (string.IsNullOrWhiteSpace(confirmedUrl))
        {
            Console.WriteLine($"استجاب Discord بنجاح لكن دون رابط صورة: {responseText}");
            await File.WriteAllTextAsync(Path.Combine(DataDir, "discord-upload-error.txt"), responseText);
            return null;
        }

        var oldErrorFile = Path.Combine(DataDir, "discord-upload-error.txt");
        if (File.Exists(oldErrorFile)) File.Delete(oldErrorFile);
        return confirmedUrl;
    }

    private static void CreateCompositeImage(IReadOnlyList<string> imagePaths, string outputPath)
    {
        if (imagePaths.Count == 0) return;
        var columns = (int)Math.Ceiling(Math.Sqrt(imagePaths.Count));
        var rows = (int)Math.Ceiling(imagePaths.Count / (double)columns);
        var tileSize = 512;
        using var composite = new SKBitmap(columns * tileSize, rows * tileSize,
            SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(composite);
        canvas.Clear(SKColors.Transparent);
        for (var i = 0; i < imagePaths.Count; i++)
        {
            using var source = SKBitmap.Decode(imagePaths[i]);
            if (source is null) continue;
            var destination = new SKRect((i % columns) * tileSize, (i / columns) * tileSize,
                (i % columns + 1) * tileSize, (i / columns + 1) * tileSize);
            canvas.DrawBitmap(source, destination);
        }
        canvas.Flush();
        EncodeCompositePng(composite, outputPath);
    }

    private static void EncodeCompositePng(SKBitmap source, string outputPath)
    {
        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(outputPath);
        data.SaveTo(stream);
        Console.WriteLine($"حجم صورة PNG المركبة: {new FileInfo(outputPath).Length / 1024d / 1024d:F2} MB");
    }

    private static async Task<T> GetJsonAsync<T>(string url, string? token=null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer",token);
        req.Headers.TryAddWithoutValidation("X-EpicGames-Language","ar");
        var res=await Http.SendAsync(req); var text=await res.Content.ReadAsStringAsync();
        if(!res.IsSuccessStatusCode) throw new Exception(text);
        return JsonConvert.DeserializeObject<T>(text)!;
    }
    private static async Task<byte[]> GetBytesAsync(string url, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await Http.SendAsync(req);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        if (!res.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(bytes);
            throw new HttpRequestException(
                $"GET {url} failed: {(int)res.StatusCode} {res.ReasonPhrase}\n" +
                text[..Math.Min(text.Length, 2000)]);
        }
        return bytes;
    }
    private static string Sanitize(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static async Task<UAthenaItemShopOfferDisplayData> LoadDisplayAssetByMountedPathAsync(
        HybridFileProvider provider, string objectPath, string objectName)
    {
        var packagePath = objectPath.Split('.')[0].TrimStart('/').Replace('\\', '/');
        var normalizedPackagePath = packagePath.TrimStart('/');

        // Prefer the Cloud/NewDisplayAssets copy over an older base-game package
        // when the same package name exists in more than one mounted container.
        var expectedFilePath = normalizedPackagePath + ".uasset";
        var candidates = provider.Files.Values
            // Do not accidentally pass the package's .uexp/.ubulk sidecar to
            // LoadPackageAsync. Select the exact mounted package file.
            .Where(file => file.Path.Replace('\\', '/')
                .EndsWith(expectedFilePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.Path.Replace('\\', '/')
                .Contains("/NewDisplayAssets/", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(file => file.Path.Replace('\\', '/')
                .Contains("/OfferCatalog/", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Console.WriteLine($"DisplayAsset {objectName}: {candidates.Length} mounted candidates");
        foreach (var candidate in candidates.Take(10))
            Console.WriteLine($"  -> {candidate.Path}");

        var mountedFile = candidates.FirstOrDefault();
        if (mountedFile is null)
            return await provider.LoadPackageObjectAsync<UAthenaItemShopOfferDisplayData>(objectPath);

        var package = await provider.LoadPackageAsync(mountedFile);
        var exports = package.GetExports().OfType<UAthenaItemShopOfferDisplayData>().ToArray();
        return exports.FirstOrDefault()
               ?? throw new InvalidDataException($"لم يوجد Display Asset داخل الملف المركب: {mountedFile.Path}");
    }

    private static async Task<T?> TryLoadObjectByMountedPathAsync<T>(
        HybridFileProvider provider, string objectPath) where T : UObject
    {
        if (string.IsNullOrWhiteSpace(objectPath)) return null;

        var normalizedObjectPath = objectPath.Replace('\\', '/').Trim();
        var dotIndex = normalizedObjectPath.LastIndexOf('.');
        var slashIndex = normalizedObjectPath.LastIndexOf('/');
        var packagePath = dotIndex > slashIndex
            ? normalizedObjectPath[..dotIndex]
            : normalizedObjectPath;
        var objectName = dotIndex > slashIndex
            ? normalizedObjectPath[(dotIndex + 1)..]
            : Path.GetFileName(packagePath);
        var expectedFilePath = packagePath.TrimStart('/') + ".uasset";

        var candidates = provider.Files.Values
            .Where(file => file.Path.Replace('\\', '/').EndsWith(
                expectedFilePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.Path.Replace('\\', '/').Contains(
                "/OfferCatalog/", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var candidate in candidates)
        {
            try
            {
                var package = await provider.LoadPackageAsync(candidate);
                var exports = package.GetExports().OfType<T>().ToArray();
                var exact = exports.FirstOrDefault(export => export.Name.Equals(
                    objectName, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return exact;
                if (exports.Length == 1) return exports[0];
            }
            catch (Exception error)
            {
                Console.WriteLine(
                    $"تعذر تحميل الملف المركب {candidate.Path}: {error.Message}");
            }
        }

        Console.WriteLine(
            $"لم يوجد ملف uasset مركب للمسار {objectPath} (expected suffix: {expectedFilePath}).");
        return null;
    }

    private static bool IsKnownPlaceholderRenderPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Contains("T_UI_PlaceholderCube", StringComparison.OrdinalIgnoreCase);

    private static void ClearPreviousGeneratedFiles(string folder)
    {
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(path);
            var generated =
                name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                (name.StartsWith("render_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ||
                (name.StartsWith("item_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ||
                (name.StartsWith("presentation_", StringComparison.OrdinalIgnoreCase) && name.EndsWith("_error.txt", StringComparison.OrdinalIgnoreCase)) ||
                (name.StartsWith("presentation_", StringComparison.OrdinalIgnoreCase) && name.EndsWith("_placeholder.txt", StringComparison.OrdinalIgnoreCase)) ||
                (name.StartsWith("presentation_", StringComparison.OrdinalIgnoreCase) && name.EndsWith("_no-image.txt", StringComparison.OrdinalIgnoreCase)) ||
                name.Equals("error.txt", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("display-asset-debug.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("display-asset-debug-error.txt", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("offer.json", StringComparison.OrdinalIgnoreCase);
            if (generated) File.Delete(path);
        }
    }
    private static async Task<bool> SaveTextureAsync(UTexture2D texture,string path)
    {
        using var bitmap = texture.Decode()?.ToSkBitmap(); if (bitmap is null || IsBlankPlaceholder(bitmap)) return false;
        if (File.Exists(WatermarkFile))
        {
            using var watermark = SKBitmap.Decode(WatermarkFile);
            if (watermark is not null)
            {
                using var canvas = new SKCanvas(bitmap);
                var targetWidth = bitmap.Width * 0.27f;
                var targetHeight = targetWidth * watermark.Height / watermark.Width;
                var margin = Math.Max(3f, bitmap.Width * 0.008f);
                var destination = new SKRect(bitmap.Width - targetWidth - margin, margin,
                    bitmap.Width - margin, margin + targetHeight);
                using var watermarkPaint = new SKPaint
                {
                    Color = SKColors.White.WithAlpha(153), // 60% opacity
                    IsAntialias = true,
                    FilterQuality = SKFilterQuality.High
                };
                canvas.DrawBitmap(watermark, destination, watermarkPaint);
                canvas.Flush();
            }
        }
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png,100);
        await using var stream=File.Create(path); data.SaveTo(stream); return true;
    }

    private static bool IsBlankPlaceholder(SKBitmap bitmap)
    {
        var stepX = Math.Max(1, bitmap.Width / 32);
        var stepY = Math.Max(1, bitmap.Height / 32);
        var samples = 0;
        var transparent = 0;
        var opaqueWhite = 0;
        for (var y = 0; y < bitmap.Height; y += stepY)
        for (var x = 0; x < bitmap.Width; x += stepX)
        {
            var color = bitmap.GetPixel(x, y);
            samples++;
            if (color.Alpha < 8) transparent++;
            if (color.Alpha > 247 && color.Red > 248 && color.Green > 248 && color.Blue > 248)
                opaqueWhite++;
        }
        // A rendered emote is commonly a solid white silhouette over transparency.
        // Never combine transparent and white pixels into one "blank" score.
        return transparent >= samples * 0.999 || opaqueWhite >= samples * 0.995;
    }
}
