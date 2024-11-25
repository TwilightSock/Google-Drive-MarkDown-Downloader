using UnityEditor;
using UnityEngine;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using System.IO;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Diagnostics;
using Google.Apis.Auth.OAuth2.Flows;
using System.Threading;
using System.Collections;
using Google.Apis.Drive.v3.Data;

public class GoogleDriveDownloader : EditorWindow
{
    private string settingsFileName = "/Settings.json";
    //https://docs.google.com/feeds/download/documents/export/Export?id=1FeBb9ekjFhcTWROqUxhPzclEFH-ztEydtGgc0n2Jd7M&exportFormat=md
    private string folderName = "docs"; // Folder name in Google Drive
    private string downloadPath = "Assets/DownloadedDocs"; // Local folder path

    [MenuItem("Tools/Google Drive Downloader")]
    public static void ShowWindow()
    {
        GetWindow<GoogleDriveDownloader>("Google Drive Downloader");
    }

    private void OnGUI()
    {
        folderName = EditorGUILayout.TextField("Folder Name (Optional)", folderName);
        downloadPath = EditorGUILayout.TextField("Download Path", downloadPath);

        if(GUILayout.Button("Load Settings"))
        {
            if(System.IO.File.Exists(downloadPath + settingsFileName))
            {
                SettingsManager _settingsManager = LoadSettingsManager(); 
                folderName = _settingsManager.FolderName;
                downloadPath = _settingsManager.DownloadPath;
         }   
        }

        if(GUILayout.Button("Save Settings"))
        {
            if(!Directory.Exists(downloadPath)) Directory.CreateDirectory(downloadPath);
            SaveSettings();
            AssetDatabase.Refresh();
        }
        if (GUILayout.Button("Download Files"))
        {
            DriveService driveService = InitializeDriveService();
            DownloadMarkdownFiles(driveService);
        }

       
    }

    private void SaveSettings()
    {
        SettingsManager _settings = new SettingsManager();
        _settings.FolderName = folderName;
        _settings.DownloadPath = downloadPath;
        string _settingsData = JsonUtility.ToJson(_settings);
        string _filePath = downloadPath + settingsFileName;
        System.IO.File.WriteAllText(_filePath,_settingsData);
    }

    private SettingsManager LoadSettingsManager()
    {
        string __settingsData = System.IO.File.ReadAllText(downloadPath + settingsFileName);
        SettingsManager _settingsManager = JsonUtility.FromJson<SettingsManager>(__settingsData);
        return _settingsManager;
    }

    private DriveService InitializeDriveService()
    {
        string clientId = "";
        string clientSecret = "";
        string[] scopes = { DriveService.Scope.Drive };

        ClientSecrets secrets = new ClientSecrets()
        {
            ClientId = clientId,
            ClientSecret = clientSecret
        };
        
        UserCredential credential =  GoogleWebAuthorizationBroker.AuthorizeAsync(secrets, 
        new[] {"https://www.googleapis.com/auth/drive.readonly"},
        "user", CancellationToken.None).Result;

        DriveService service = new DriveService(new BaseClientService.Initializer() {HttpClientInitializer = credential});
        return service;
    }

    private async void DownloadMarkdownFiles(DriveService driveService)
    {
        if (string.IsNullOrEmpty(folderName))
        {
            UnityEngine.Debug.Log("No folder specified. Downloading from root directory.");
            await DownloadFilesFromFolderAsync("root", driveService);
        }
        else
        {
            string folderId = GetFolderId(folderName,driveService);
            if (!string.IsNullOrEmpty(folderId))
            {
                await DownloadFilesFromFolderAsync(folderId,driveService);
            }
            else
            {
                UnityEngine.Debug.LogError($"Folder '{folderName}' not found in Google Drive.");
            }
        }
    }

    private string GetFolderId(string folderName,DriveService driveService)
    {
        var request = driveService.Files.List();
        request.Q = $"name='{folderName}' and mimeType='application/vnd.google-apps.folder'";
        request.Spaces = "drive";

        var result = request.Execute();
        if (result.Files != null && result.Files.Count > 0)
        {
            return result.Files[0].Id;
        }

        return null;
    }

    private async Task DownloadFilesFromFolderAsync(string folderId, DriveService driveService)
    {
        var request = driveService.Files.List();
        request.Q = $"'{folderId}' in parents";
        request.Fields = "files(id, name, mimeType)";
        var result = request.Execute();

        if (result.Files == null || result.Files.Count == 0)
        {
            UnityEngine.Debug.Log("No files found in the specified folder.");
            return;
        }

        Directory.CreateDirectory(downloadPath);

        int totalFiles = result.Files.Count;
        int currentFileIndex = 0;

        foreach (var file in result.Files)
        {
            currentFileIndex++;
            string progressMessage = $"Downloading {file.Name} ({currentFileIndex}/{totalFiles})";

            if (file.MimeType == "application/vnd.google-apps.document")
            {
                string exportUrl = $"https://docs.google.com/feeds/download/documents/export/Export?id={file.Id}&exportFormat=md";
                await DownloadFileAsync(exportUrl, file.Name, progressMessage, currentFileIndex, totalFiles, driveService);
            }
            else
            {
                UnityEngine.Debug.Log($"Skipping unsupported file: {file.Name} ({file.MimeType})");
            }
        }

        EditorUtility.ClearProgressBar();
    }

    private async Task DownloadFileAsync(string url, string fileName, string progressMessage, int currentFileIndex, int totalFiles, DriveService driveService)
    {
        try
        {
            var accessToken = ((UserCredential)driveService.HttpClientInitializer).Token.AccessToken;
            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                HttpResponseMessage response = await httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    string localFilePath = Path.Combine(downloadPath, fileName + ".md");

                    using (var fs = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var contentStream = await response.Content.ReadAsStreamAsync();
                        byte[] buffer = new byte[8192];
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            fs.Write(buffer, 0, bytesRead);

                            float progress = (float)currentFileIndex / totalFiles;
                            EditorUtility.DisplayProgressBar("Downloading Markdown Files", progressMessage, progress);
                        }
                    }

                    UnityEngine.Debug.Log($"Downloaded {fileName} as markdown.");
                }
                else
                {
                    UnityEngine.Debug.LogError($"Failed to download {fileName}. HTTP Status: {response.StatusCode}");
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"Error downloading file: {fileName}. Exception: {ex.Message}");
        }
    }
}



[Serializable]
internal struct SettingsManager
{
    [SerializeField] private string folderName;
    [SerializeField] private string downloadPath;
    public string FolderName {get => folderName; set=> folderName = value ;}
    public string DownloadPath {get => downloadPath; set => downloadPath = value;}

}