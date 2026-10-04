using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ShopStreamExtractor;

public sealed class EpicInstallItem
{
    [JsonProperty("AppName")] public string AppName { get; set; } = "";
    [JsonProperty("DisplayName")] public string DisplayName { get; set; } = "";
    [JsonProperty("InstallLocation")] public string InstallLocation { get; set; } = "";
}

public sealed record EpicSession(
    [property: JsonProperty("access_token")] string AccessToken,
    [property: JsonProperty("account_id")] string? AccountId,
    [property: JsonProperty("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonProperty("refresh_token")] string? RefreshToken,
    [property: JsonProperty("refresh_expires_at")] DateTimeOffset? RefreshExpiresAt);

public sealed record DeviceCodeResponse(
    [property: JsonProperty("device_code")] string DeviceCode,
    [property: JsonProperty("user_code")] string UserCode,
    [property: JsonProperty("verification_uri")] string VerificationUri,
    [property: JsonProperty("verification_uri_complete")] string? VerificationUriComplete,
    [property: JsonProperty("expires_in")] int ExpiresIn,
    [property: JsonProperty("interval")] int Interval);

public sealed record VersionInfo(string Version, VersionKeys Keys, VersionMappings Mappings);
public sealed record VersionKeys(MainKey MainKey, MainKey[] ExtraKeys);
public sealed record MainKey(string Key, string Guid);
public sealed record VersionMappings(string Url, string Md5Hash);
public sealed record CloudContentInfo(string ManifestPath);

public sealed record CatalogRoot(CatalogStorefront[] Storefronts);
public sealed record CatalogStorefront(string Name, CatalogEntry[] CatalogEntries);
public sealed record CatalogEntry(string OfferId, string DevName, Dictionary<string,string>? Meta, Grant[]? ItemGrants)
{
    [JsonExtensionData]
    public IDictionary<string, JToken>? AdditionalData { get; init; }
}
public sealed record Grant(string TemplateId, int Quantity);

public sealed record ExtractedOffer(string OfferId, string DevName, string? NewDisplayAssetPath,
    string[] ItemTemplateIds, string[] ExportedImages, DateTimeOffset DetectedAt);

public sealed record ExtractedOfferArtifact(
    CatalogEntry Entry,
    string Storefront,
    string DisplayAssetName,
    string ImagePath,
    string ChangeType);

public sealed record PublishedCatalogItem(
    string OfferId,
    string DevName,
    string Storefront,
    string DisplayAssetName,
    string NewDisplayAssetPath,
    string ImageFileName,
    string ImageUrl,
    string ChangeType,
    Grant[] ItemGrants,
    Dictionary<string, string>? Meta,
    IDictionary<string, JToken>? AdditionalData);

public sealed record CatalogUpdateFile(
    DateTimeOffset DetectedAt,
    int ItemCount,
    string CompositeImageFile,
    string CompositeImageUrl,
    PublishedCatalogItem[] Items);

public sealed record CatalogSnapshotRecord(
    string OfferId,
    string AssetKey,
    string Signature);

public sealed record CatalogSnapshotState(
    DateTimeOffset CapturedAt,
    Dictionary<string, CatalogSnapshotRecord> Offers);

public sealed record DisplayAssetCacheState(
    DateTimeOffset UpdatedAt,
    HashSet<string> SeenPaths);

public sealed record CatalogOfferCacheState(
    DateTimeOffset UpdatedAt,
    HashSet<string> SeenOfferKeys);

public sealed record CloudDisplayAssetCacheState(
    DateTimeOffset UpdatedAt,
    Dictionary<string, string> Files,
    int SignatureVersion = 1);
