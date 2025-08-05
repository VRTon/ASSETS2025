using UnityEngine;
using UnityEditor;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine.Networking;
using System;
using System.Threading.Tasks;

// This script is used to download .unitypackage files from a specified URL and import them into the Unity project.
// It will list a set of assets available for download and allow the user to select and download them.
// The downloaded assets will be stored in the project's Assets folder.
// The menu will display picture, name, description and version of the assets.

[System.Serializable]
public class AssetInfo
{
    public string name;
    public string description;
    public string version;
    public string downloadUrl;
    public string imageUrl;
    public string category;
    public long fileSize;
    [System.NonSerialized]
    public Texture2D previewImage;
    [System.NonSerialized]
    public bool isDownloading;
    [System.NonSerialized]
    public float downloadProgress;
}

[System.Serializable]
public class AssetCatalog
{
    public List<AssetInfo> assets = new List<AssetInfo>();
}

public class AssetDownloader : EditorWindow
{
    private AssetCatalog catalog;
    private Vector2 scrollPosition;
    private string catalogUrl = "https://vrton.org/data/catalog.json";
    private string tempDownloadPath;
    private bool isLoadingCatalog = false;
    private string statusMessage = "";
    private GUIStyle cardStyle;
    private GUIStyle titleStyle;
    private Texture2D bannerTexture;
    private Dictionary<string, UnityWebRequest> activeDownloads = new Dictionary<string, UnityWebRequest>();
    private HashSet<string> loadingImages = new HashSet<string>();
    private HashSet<string> loadingSizes = new HashSet<string>();
    private const int MAX_DOWNLOAD_SIZE_MB = 500; // Maximum download size in MB
    private const int REQUEST_TIMEOUT_SECONDS = 30;

    [MenuItem("VRTon/Asset Downloader")]
    public static void ShowWindow()
    {
        AssetDownloader window = GetWindow<AssetDownloader>("Asset Downloader");
        window.minSize = new Vector2(600, 400);
        window.Show();
    }

    private async void OnEnable()
    {
        tempDownloadPath = Path.Combine(Application.temporaryCachePath, "AssetDownloader");
        if (!Directory.Exists(tempDownloadPath))
        {
            Directory.CreateDirectory(tempDownloadPath);
        }
        
        // Load banner texture
        bannerTexture = Resources.Load<Texture2D>("VRTonBanner");
        
        await LoadCatalog();
        EditorApplication.update += UpdateDownloads;
    }

    private void OnDisable()
    {
        EditorApplication.update -= UpdateDownloads;
        
        // Cancel any active downloads
        foreach (var download in activeDownloads.Values)
        {
            if (download != null)
            {
                download.Dispose();
            }
        }
        activeDownloads.Clear();
        loadingImages.Clear();
        loadingSizes.Clear();
        
        // Clean up preview images to prevent memory leaks
        if (catalog?.assets != null)
        {
            foreach (var asset in catalog.assets)
            {
                if (asset.previewImage != null)
                {
                    DestroyImmediate(asset.previewImage);
                    asset.previewImage = null;
                }
            }
        }
        
        // Clean up banner texture if it was loaded
        if (bannerTexture != null)
        {
            // Don't destroy Resources.Load textures, Unity handles them
            bannerTexture = null;
        }
    }

    private void InitializeStyles()
    {
        if (cardStyle == null)
        {
            cardStyle = new GUIStyle(GUI.skin.box);
            cardStyle.padding = new RectOffset(10, 10, 10, 10);
            cardStyle.margin = new RectOffset(5, 5, 5, 5);
        }

        if (titleStyle == null)
        {
            titleStyle = new GUIStyle(EditorStyles.boldLabel);
            titleStyle.fontSize = 14;
        }
    }

    private void OnGUI()
    {
        InitializeStyles();

        EditorGUILayout.BeginVertical();

        // Header
        EditorGUILayout.Space(10);
        
        // Banner image
        if (bannerTexture != null)
        {
            // Calculate aspect ratio and fit to window width with max height
            float aspectRatio = (float)bannerTexture.width / bannerTexture.height;
            float maxBannerHeight = 120f;
            float bannerWidth = position.width - 20f; // Leave some margin
            float bannerHeight = bannerWidth / aspectRatio;
            
            // Limit height and adjust width if needed
            if (bannerHeight > maxBannerHeight)
            {
                bannerHeight = maxBannerHeight;
                bannerWidth = bannerHeight * aspectRatio;
            }
            
            // Center the banner
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(bannerTexture, GUILayout.Width(bannerWidth), GUILayout.Height(bannerHeight));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }
        else
        {
            // Fallback to text if banner not found
            EditorGUILayout.LabelField("VRTon Asset Downloader", EditorStyles.largeLabel);
        }
        
        EditorGUILayout.Space(5);

        // Refresh button
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Refresh Catalog", GUILayout.Width(100)))
        {
            _ = LoadCatalog(); // Fire and forget with discard
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(10);

        // Status message
        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
        }

        // Loading indicator
        if (isLoadingCatalog)
        {
            EditorGUILayout.LabelField("Loading catalog...", EditorStyles.centeredGreyMiniLabel);
            return;
        }

        if (catalog == null || catalog.assets == null || catalog.assets.Count == 0)
        {
            EditorGUILayout.LabelField("No assets available. Check your catalog URL.", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndVertical();
            return;
        }

        EditorGUILayout.Space(10);

        // Asset list
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        
        foreach (var asset in catalog.assets)
        {
            DrawAssetCard(asset);
            EditorGUILayout.Space(5);
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    private void DrawAssetCard(AssetInfo asset)
    {
        EditorGUILayout.BeginVertical(cardStyle);

        EditorGUILayout.BeginHorizontal();

        // Preview image
        if (asset.previewImage != null)
        {
            GUILayout.Label(asset.previewImage, GUILayout.Width(64), GUILayout.Height(64));
        }
        else
        {
            GUILayout.Box("No Image", GUILayout.Width(64), GUILayout.Height(64));
            if (!string.IsNullOrEmpty(asset.imageUrl))
            {
                _ = LoadPreviewImage(asset); // Fire and forget
            }
        }

        // Asset info
        EditorGUILayout.BeginVertical();
        EditorGUILayout.LabelField(asset.name, titleStyle);
        EditorGUILayout.LabelField($"Version: {asset.version}");
        EditorGUILayout.LabelField($"Category: {asset.category}");
        if (asset.fileSize > 0)
        {
            EditorGUILayout.LabelField($"Size: {FormatFileSize(asset.fileSize)}");
        }
        else if (!string.IsNullOrEmpty(asset.downloadUrl))
        {
            EditorGUILayout.LabelField("Size: Calculating...");
            _ = LoadFileSizeDynamically(asset); // Fire and forget
        }
        EditorGUILayout.LabelField(asset.description, EditorStyles.wordWrappedLabel);
        EditorGUILayout.EndVertical();

        // Download button and progress
        EditorGUILayout.BeginVertical(GUILayout.Width(100));
        
        if (asset.isDownloading)
        {
            EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(), asset.downloadProgress, "Downloading...");
        }
        else
        {
            if (GUILayout.Button("Download"))
            {
                _ = DownloadAsset(asset); // Fire and forget
            }
        }
        
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
    }

    private async Task LoadCatalog()
    {
        if (isLoadingCatalog) return; // Prevent multiple simultaneous loads
        
        isLoadingCatalog = true;
        statusMessage = "Loading catalog...";

        UnityWebRequest request = null;
        try
        {
            request = UnityWebRequest.Get(catalogUrl);
            request.timeout = REQUEST_TIMEOUT_SECONDS;
            var operation = request.SendWebRequest();

            // Wait with timeout
            var startTime = EditorApplication.timeSinceStartup;
            while (!operation.isDone && (EditorApplication.timeSinceStartup - startTime) < REQUEST_TIMEOUT_SECONDS)
            {
                await Task.Delay(100);
            }

            if (!operation.isDone)
            {
                request.Abort();
                statusMessage = "Request timed out. Please check your internet connection.";
                return;
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                string jsonData = request.downloadHandler.text;
                
                // Validate JSON data
                if (string.IsNullOrEmpty(jsonData))
                {
                    statusMessage = "Received empty catalog data.";
                    return;
                }
                
                // If this is a GitHub API response, extract the content
                if (catalogUrl.Contains("api.github.com"))
                {
                    try
                    {
                        GitHubFileResponse response = JsonUtility.FromJson<GitHubFileResponse>(jsonData);
                        if (!string.IsNullOrEmpty(response.content))
                        {
                            jsonData = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(response.content));
                        }
                    }
                    catch (Exception githubParseEx)
                    {
                        statusMessage = $"Failed to parse GitHub API response: {githubParseEx.Message}";
                        Debug.LogError($"GitHub API parsing error: {githubParseEx}");
                        return;
                    }
                }

                try
                {
                    catalog = JsonUtility.FromJson<AssetCatalog>(jsonData);
                    
                    if (catalog?.assets == null)
                    {
                        statusMessage = "Invalid catalog format: no assets found.";
                        return;
                    }
                    
                    // Filter out assets with invalid download URLs
                    catalog.assets = catalog.assets.FindAll(asset => IsValidDownloadUrl(asset.downloadUrl));
                    
                    statusMessage = $"Loaded {catalog.assets.Count} valid assets";
                }
                catch (Exception jsonParseEx)
                {
                    statusMessage = $"Failed to parse catalog JSON: {jsonParseEx.Message}";
                    Debug.LogError($"JSON parsing error: {jsonParseEx}");
                }
            }
            else
            {
                statusMessage = $"Failed to load catalog: {request.error}";
                Debug.LogError($"Catalog loading failed: {request.error}");
            }
        }
        catch (Exception e)
        {
            statusMessage = $"Error loading catalog: {e.Message}";
            Debug.LogError($"Catalog loading error: {e}");
        }
        finally
        {
            request?.Dispose();
            isLoadingCatalog = false;
            Repaint();
        }
    }

    private async Task LoadPreviewImage(AssetInfo asset)
    {
        if (string.IsNullOrEmpty(asset.imageUrl) || asset.previewImage != null || loadingImages.Contains(asset.imageUrl))
            return;

        loadingImages.Add(asset.imageUrl);
        UnityWebRequest request = null;
        
        try
        {
            request = UnityWebRequestTexture.GetTexture(asset.imageUrl);
            request.timeout = REQUEST_TIMEOUT_SECONDS;
            var operation = request.SendWebRequest();

            var startTime = EditorApplication.timeSinceStartup;
            while (!operation.isDone && (EditorApplication.timeSinceStartup - startTime) < REQUEST_TIMEOUT_SECONDS)
            {
                await Task.Delay(100);
            }

            if (!operation.isDone)
            {
                request.Abort();
                return;
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                asset.previewImage = ((DownloadHandlerTexture)request.downloadHandler).texture;
                EditorApplication.delayCall += Repaint; // Thread-safe UI update
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to load preview image for {asset.name}: {e.Message}");
        }
        finally
        {
            request?.Dispose();
            loadingImages.Remove(asset.imageUrl);
        }
    }

    private async Task LoadFileSizeDynamically(AssetInfo asset)
    {
        if (string.IsNullOrEmpty(asset.downloadUrl) || asset.fileSize > 0 || loadingSizes.Contains(asset.downloadUrl))
            return;

        loadingSizes.Add(asset.downloadUrl);
        UnityWebRequest request = null;
        
        try
        {
            request = UnityWebRequest.Head(asset.downloadUrl);
            request.timeout = REQUEST_TIMEOUT_SECONDS;
            var operation = request.SendWebRequest();

            var startTime = EditorApplication.timeSinceStartup;
            while (!operation.isDone && (EditorApplication.timeSinceStartup - startTime) < REQUEST_TIMEOUT_SECONDS)
            {
                await Task.Delay(100);
            }

            if (!operation.isDone)
            {
                request.Abort();
                return;
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                string contentLength = request.GetResponseHeader("Content-Length");
                if (!string.IsNullOrEmpty(contentLength) && long.TryParse(contentLength, out long size))
                {
                    asset.fileSize = size;
                    EditorApplication.delayCall += Repaint; // Thread-safe UI update
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to get file size for {asset.name}: {e.Message}");
        }
        finally
        {
            request?.Dispose();
            loadingSizes.Remove(asset.downloadUrl);
        }
    }

    private async Task DownloadAsset(AssetInfo asset)
    {
        if (asset.isDownloading || string.IsNullOrEmpty(asset.downloadUrl))
            return;

        // Validate file size before download
        if (asset.fileSize > MAX_DOWNLOAD_SIZE_MB * 1024 * 1024)
        {
            statusMessage = $"File too large: {asset.name} ({FormatFileSize(asset.fileSize)}) exceeds {MAX_DOWNLOAD_SIZE_MB}MB limit";
            return;
        }

        asset.isDownloading = true;
        asset.downloadProgress = 0f;
        
        // Sanitize filename to prevent path traversal
        string sanitizedName = string.Join("_", asset.name.Split(Path.GetInvalidFileNameChars()));
        string sanitizedVersion = string.Join("_", asset.version.Split(Path.GetInvalidFileNameChars()));
        string fileName = $"{sanitizedName}_{sanitizedVersion}.unitypackage";
        string filePath = Path.Combine(tempDownloadPath, fileName);

        UnityWebRequest request = null;
        try
        {
            request = UnityWebRequest.Get(asset.downloadUrl);
            request.timeout = REQUEST_TIMEOUT_SECONDS;
            activeDownloads[asset.name] = request;
            
            var operation = request.SendWebRequest();
            var startTime = EditorApplication.timeSinceStartup;

            while (!operation.isDone && (EditorApplication.timeSinceStartup - startTime) < REQUEST_TIMEOUT_SECONDS * 10) // Longer timeout for downloads
            {
                asset.downloadProgress = operation.progress;
                EditorApplication.delayCall += Repaint;
                await Task.Delay(100);
            }

            if (!operation.isDone)
            {
                request.Abort();
                statusMessage = $"Download timeout for {asset.name}";
                return;
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                // Validate download size
                byte[] data = request.downloadHandler.data;
                if (data == null || data.Length == 0)
                {
                    statusMessage = $"Downloaded file is empty: {asset.name}";
                    return;
                }

                if (data.Length > MAX_DOWNLOAD_SIZE_MB * 1024 * 1024)
                {
                    statusMessage = $"Downloaded file too large: {asset.name}";
                    return;
                }

                // Write file safely
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllBytes(filePath, data);
                
                // Validate file exists and has content
                if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                {
                    statusMessage = $"Failed to save download: {asset.name}";
                    return;
                }
                
                // Import the package
                try
                {
                    AssetDatabase.ImportPackage(filePath, true);
                    statusMessage = $"Successfully downloaded and imported {asset.name}";
                }
                catch (Exception importEx)
                {
                    statusMessage = $"Download successful but import failed for {asset.name}: {importEx.Message}";
                    Debug.LogError($"Import failed for {asset.name}: {importEx}");
                }
                
                // Clean up the temporary file
                try
                {
                    File.Delete(filePath);
                }
                catch (Exception deleteEx)
                {
                    Debug.LogWarning($"Failed to delete temporary file {filePath}: {deleteEx.Message}");
                }
            }
            else
            {
                statusMessage = $"Failed to download {asset.name}: {request.error}";
                Debug.LogError($"Download failed for {asset.name}: {request.error}");
            }
        }
        catch (Exception e)
        {
            statusMessage = $"Error downloading {asset.name}: {e.Message}";
            Debug.LogError($"Download error for {asset.name}: {e}");
        }
        finally
        {
            activeDownloads.Remove(asset.name);
            request?.Dispose();
            asset.isDownloading = false;
            EditorApplication.delayCall += Repaint;
        }
    }

    private void UpdateDownloads()
    {
        bool needsRepaint = false;
        
        foreach (var asset in catalog?.assets ?? new List<AssetInfo>())
        {
            if (asset.isDownloading)
            {
                needsRepaint = true;
            }
        }

        if (needsRepaint)
        {
            Repaint();
        }
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private bool IsValidDownloadUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        // Check if the URL is properly formatted
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uriResult))
            return false;

        // Check if it's HTTP or HTTPS
        if (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps)
            return false;

        // Block localhost and private IP ranges for security
        string host = uriResult.Host.ToLower();
        if (host == "localhost" || host == "127.0.0.1" || host.StartsWith("192.168.") || 
            host.StartsWith("10.") || host.StartsWith("172."))
        {
            // Allow localhost only in development (when catalog URL is localhost)
            if (!catalogUrl.Contains("127.0.0.1") && !catalogUrl.Contains("localhost"))
                return false;
        }

        // More strict validation for downloadable content
        string lowerUrl = url.ToLower();
        bool hasValidExtension = lowerUrl.EndsWith(".unitypackage") || 
                                lowerUrl.EndsWith(".zip") || 
                                lowerUrl.EndsWith(".tar.gz");
        
        bool hasValidPath = lowerUrl.Contains("/download") || 
                           lowerUrl.Contains("/releases/") ||
                           lowerUrl.Contains("/attachments/") ||
                           (lowerUrl.Contains("github.com") && (lowerUrl.Contains("/releases/") || lowerUrl.Contains("/download/")));

        return hasValidExtension || hasValidPath;
    }

    [System.Serializable]
    private class GitHubFileResponse
    {
        public string content;
        public string encoding;
    }
}
