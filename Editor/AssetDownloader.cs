using UnityEngine;
using UnityEditor;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine.Networking;
using System;

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
    private string catalogUrl = "http://vrton.org/data/catalog.json";
    private string tempDownloadPath;
    private bool isLoadingCatalog = false;
    private string statusMessage = "";
    private GUIStyle cardStyle;
    private GUIStyle titleStyle;
    private Texture2D bannerTexture;
    private Dictionary<string, UnityWebRequest> activeDownloads = new Dictionary<string, UnityWebRequest>();

    [MenuItem("VRTon/Asset Downloader")]
    public static void ShowWindow()
    {
        AssetDownloader window = GetWindow<AssetDownloader>("Asset Downloader");
        window.minSize = new Vector2(600, 400);
        window.Show();
    }

    private void OnEnable()
    {
        tempDownloadPath = Path.Combine(Application.temporaryCachePath, "AssetDownloader");
        if (!Directory.Exists(tempDownloadPath))
        {
            Directory.CreateDirectory(tempDownloadPath);
        }
        
        // Load banner texture
        bannerTexture = Resources.Load<Texture2D>("VRTonBanner");
        
        LoadCatalog();
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
            LoadCatalog();
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
                LoadPreviewImage(asset);
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
            LoadFileSizeDynamically(asset);
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
                DownloadAsset(asset);
            }
        }
        
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
    }

    private async void LoadCatalog()
    {
        isLoadingCatalog = true;
        statusMessage = "Loading catalog...";

        try
        {
            UnityWebRequest request = UnityWebRequest.Get(catalogUrl);
            var operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                await System.Threading.Tasks.Task.Delay(100);
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                string jsonData = request.downloadHandler.text;
                
                // If this is a GitHub API response, extract the content
                if (catalogUrl.Contains("api.github.com"))
                {
                    GitHubFileResponse response = JsonUtility.FromJson<GitHubFileResponse>(jsonData);
                    if (!string.IsNullOrEmpty(response.content))
                    {
                        jsonData = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(response.content));
                    }
                }

                catalog = JsonUtility.FromJson<AssetCatalog>(jsonData);
                
                // Filter out assets with invalid download URLs
                if (catalog?.assets != null)
                {
                    catalog.assets = catalog.assets.FindAll(asset => IsValidDownloadUrl(asset.downloadUrl));
                }
                
                statusMessage = $"Loaded {catalog.assets.Count} assets";
            }
            else
            {
                statusMessage = $"Failed to load catalog: {request.error}";
                Debug.LogError($"Catalog loading failed: {request.error}");
            }

            request.Dispose();
        }
        catch (Exception e)
        {
            statusMessage = $"Error loading catalog: {e.Message}";
            Debug.LogError($"Catalog loading error: {e}");
        }
        finally
        {
            isLoadingCatalog = false;
            Repaint();
        }
    }

    private async void LoadPreviewImage(AssetInfo asset)
    {
        if (string.IsNullOrEmpty(asset.imageUrl) || asset.previewImage != null)
            return;

        try
        {
            UnityWebRequest request = UnityWebRequestTexture.GetTexture(asset.imageUrl);
            var operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                await System.Threading.Tasks.Task.Delay(100);
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                asset.previewImage = ((DownloadHandlerTexture)request.downloadHandler).texture;
                Repaint();
            }

            request.Dispose();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to load preview image for {asset.name}: {e.Message}");
        }
    }

    private async void LoadFileSizeDynamically(AssetInfo asset)
    {
        if (string.IsNullOrEmpty(asset.downloadUrl) || asset.fileSize > 0)
            return;

        try
        {
            UnityWebRequest request = UnityWebRequest.Head(asset.downloadUrl);
            var operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                await System.Threading.Tasks.Task.Delay(100);
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                string contentLength = request.GetResponseHeader("Content-Length");
                if (!string.IsNullOrEmpty(contentLength) && long.TryParse(contentLength, out long size))
                {
                    asset.fileSize = size;
                    Repaint();
                }
            }

            request.Dispose();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to get file size for {asset.name}: {e.Message}");
        }
    }

    private async void DownloadAsset(AssetInfo asset)
    {
        if (asset.isDownloading || string.IsNullOrEmpty(asset.downloadUrl))
            return;

        asset.isDownloading = true;
        asset.downloadProgress = 0f;
        
        string fileName = $"{asset.name}_{asset.version}.unitypackage";
        string filePath = Path.Combine(tempDownloadPath, fileName);

        try
        {
            UnityWebRequest request = UnityWebRequest.Get(asset.downloadUrl);
            activeDownloads[asset.name] = request;
            
            var operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                asset.downloadProgress = operation.progress;
                Repaint();
                await System.Threading.Tasks.Task.Delay(100);
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                File.WriteAllBytes(filePath, request.downloadHandler.data);
                
                // Import the package
                AssetDatabase.ImportPackage(filePath, true);
                
                statusMessage = $"Successfully downloaded and imported {asset.name}";
                
                // Clean up the temporary file
                File.Delete(filePath);
            }
            else
            {
                statusMessage = $"Failed to download {asset.name}: {request.error}";
                Debug.LogError($"Download failed for {asset.name}: {request.error}");
            }

            activeDownloads.Remove(asset.name);
            request.Dispose();
        }
        catch (Exception e)
        {
            statusMessage = $"Error downloading {asset.name}: {e.Message}";
            Debug.LogError($"Download error for {asset.name}: {e}");
        }
        finally
        {
            asset.isDownloading = false;
            Repaint();
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

        // Additional checks for common file extensions that indicate downloadable content
        string lowerUrl = url.ToLower();
        return lowerUrl.Contains(".unitypackage") || 
               lowerUrl.Contains("/download") || 
               lowerUrl.Contains("/releases/") ||
               lowerUrl.Contains("github.com") ||
               lowerUrl.EndsWith(".zip") ||
               lowerUrl.EndsWith(".tar.gz");
    }

    [System.Serializable]
    private class GitHubFileResponse
    {
        public string content;
        public string encoding;
    }
}
