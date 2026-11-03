using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using renderappio_visualiser.Publish;

namespace renderappio_visualiser
{
    public partial class MainWindow : Window
    {
        // RENDERAPP_URL points the app at another instance, a local server for one.
        static readonly Uri host = new(Environment.GetEnvironmentVariable("RENDERAPP_URL") ?? "https://beta.renderapp.io");
        static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
        readonly RenderappSession session = new(http, host);
        JObject? currentRio;
        string currentName = "";
        CancellationTokenSource? publishing;

        public MainWindow()
        {
            InitializeComponent();

            // Menu events
            exitMenuItem.Click += (s, e) => Close();
            discordMenuItem.Click += (s, e) => VisitURL("https://discord.gg/s8BSKwuw");

            importRioMenuItem.Click += async (s, e) => await ImportRioFile();
            importImagesStillMenuItem.Click += async (s, e) => await FilesToRio(0);
            importFoldersStillMenuItem.Click += async (s, e) => await DirectoryToRio(0);
            importImagesSpinMenuItem.Click += async (s, e) => await FilesToRio(1);
            importFoldersSpinMenuItem.Click += async (s, e) => await DirectoryToRio(1);
            importImagesStillsMenuItem.Click += async (s, e) => await FilesToRio(2);
            importFoldersStillsMenuItem.Click += async (s, e) => await DirectoryToRio(2);
            exportRioMenuItem.Click += async (s, e) => { /* TODO: needs data from UI */ };
            saveMenuItem.Click += (s, e) => { /* TODO: needs native UI */ };
            saveAsMenuItem.Click += (s, e) => { /* TODO: needs native UI */ };
            newMenuItem.Click += (s, e) => { /* TODO: needs native UI */ };
            addVersionMenuItem.Click += (s, e) => { /* TODO: needs native UI */ };
            publishMenuItem.Click += async (s, e) => await PublishAsync();
            cancelPublishMenuItem.Click += (s, e) => publishing?.Cancel();
            signOutMenuItem.Click += (s, e) =>
            {
                session.SignOut();
                UpdateStatus("Signed out of Renderapp.");
            };
        }

        private void Load(JObject rio, string name)
        {
            currentRio = rio;
            currentName = name;
            publishMenuItem.IsEnabled = publishing == null;
        }

        private async Task PublishAsync()
        {
            if (currentRio is not { } rio || publishing != null) return;
            var name = currentName;
            var cacheDir = rio["CacheDir"]?.Value<string>();
            if (string.IsNullOrEmpty(cacheDir) || !Directory.Exists(cacheDir))
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select the folder holding this project's files"
                });
                if (folders.Count == 0 || publishing != null) return;
                rio["CacheDir"] = Path.TrimEndingDirectorySeparator(folders[0].Path.LocalPath).Replace("\\", "/");
            }

            using var cancel = new CancellationTokenSource();
            publishing = cancel;
            publishMenuItem.IsEnabled = false;
            cancelPublishMenuItem.IsEnabled = true;
            try
            {
                var progress = new Progress<UploadProgress>(p => UpdateStatus(
                    $"Uploading: {p.FilesDone} of {p.FilesTotal} files, {p.BytesDone / 1048576} of {p.BytesTotal / 1048576} MB"));
                var destination = await new Publisher(http, host, session).PublishAsync(rio, name, stage => UpdateStatus(stage switch
                {
                    PublishStage.SigningIn => "Signing in to Renderapp. If your browser opens, sign in there.",
                    PublishStage.ChoosingDestination => "Choose where to publish in your browser.",
                    _ => "Uploading.",
                }), progress, cancel.Token);
                UpdateStatus("Published. Renderapp is preparing it now.");
                VisitURL(new Uri(host, $"/viewer/{destination.ProjectId}/{destination.Version}").AbsoluteUri);
            }
            catch (PublishException error)
            {
                UpdateStatus(error.Message);
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("Publishing cancelled. Publish again to carry on where it stopped.");
            }
            catch (HttpRequestException)
            {
                UpdateStatus("Couldn't reach Renderapp. Publish again to carry on where it stopped.");
            }
            // This runs from a menu click, where an unhandled error would close the app.
            catch (Exception error)
            {
                Trace.TraceError(error.ToString());
                UpdateStatus($"Publishing failed: {error.Message}");
            }
            finally
            {
                publishing = null;
                publishMenuItem.IsEnabled = true;
                cancelPublishMenuItem.IsEnabled = false;
            }
        }

        // Utilities

        private void UpdateStatus(string message)
        {
            Dispatcher.UIThread.Post(() => statusText.Text = message);
        }

        private void VisitURL(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    Process.Start("xdg-open", url);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start("open", url);
            }
        }

        private string ReturnContentType(string imageURL)
        {
            string extension = Path.GetExtension(imageURL);
            return extension switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                _ => "application/octet-stream"
            };
        }

        // File operations

        private async Task ImportRioFile()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Renderapp.io file",
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Renderapp.io File") { Patterns = new[] { "*.rio" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*" } }
                }
            });

            if (files.Count > 0)
            {
                string filePath = files[0].Path.LocalPath;
                string datastring = File.ReadAllText(filePath);
                JObject data;
                try
                {
                    data = JObject.Parse(datastring);
                }
                catch (Newtonsoft.Json.JsonReaderException)
                {
                    // Not JSON: an encrypted rio, which Renderapp decrypts for a signed-in person.
                    try
                    {
                        UpdateStatus("This rio is encrypted. Sign in to Renderapp to open it.");
                        data = JObject.Parse(await RioDecryption.DecryptAsync(http, host, session, datastring, CancellationToken.None));
                    }
                    catch (Exception error) when (error is PublishException or HttpRequestException or Newtonsoft.Json.JsonReaderException)
                    {
                        UpdateStatus($"Couldn't open {Path.GetFileName(filePath)}: {error.Message}");
                        return;
                    }
                }
                Load(data, Path.GetFileNameWithoutExtension(filePath));
                UpdateStatus($"Imported: {Path.GetFileName(filePath)}");
            }
        }

        private async Task FilesToRio(int type)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select folder with images"
            });

            if (folders.Count > 0)
            {
                string folderPath = Path.TrimEndingDirectorySeparator(folders[0].Path.LocalPath);
                JObject data = LoadFromFolder.LoadFilenames(folderPath, type);
                Load(data, Path.GetFileName(folderPath));
                UpdateStatus($"Loaded {data["Maps"]?.Count() ?? 0} files from {Path.GetFileName(folderPath)}");
            }
        }

        private async Task DirectoryToRio(int type)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select folder with image directories"
            });

            if (folders.Count > 0)
            {
                string folderPath = Path.TrimEndingDirectorySeparator(folders[0].Path.LocalPath);
                JObject data = LoadFromFolder.LoadDirectory(folderPath, type);
                Load(data, Path.GetFileName(folderPath));
                UpdateStatus($"Loaded {data["Maps"]?.Count() ?? 0} files from {Path.GetFileName(folderPath)}");
            }
        }

        private async Task ExportRioFile(JObject data)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Renderapp.io file",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Renderapp.io File") { Patterns = new[] { "*.rio" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*" } }
                }
            });

            if (file != null)
            {
                File.WriteAllText(file.Path.LocalPath, data.ToString());
                UpdateStatus("Export complete!");
            }
        }

        private async Task SetCache()
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select cache directory"
            });

            if (folders.Count > 0)
            {
                string cachePath = Path.TrimEndingDirectorySeparator(folders[0].Path.LocalPath).Replace("\\", "/");
                UpdateStatus($"Cache set to: {cachePath}");
            }
        }

        internal void ImagesToBase64(JObject data)
        {
            string folderPath = data["cache"]!.Value<string>()!;
            JObject ImageUrlsPerFrame = data["imageURLsPerFrame"]!.Value<JObject>()!;
            JObject ImageDataPerFrame = new JObject();
            foreach (var obj in ImageUrlsPerFrame)
            {
                string key = obj.Key;
                JArray ImageURLs = (obj.Value as JArray)!;
                JArray ImageData = new JArray();

                for (int i = 0; i < ImageURLs.Count; i++)
                {
                    string ImageURL = Path.Combine(folderPath, ImageURLs[i]!.Value<string>()!);
                    if (File.Exists(ImageURL))
                    {
                        byte[] imageBytes = File.ReadAllBytes(ImageURL);
                        ImageData.Insert(i, $"data:{ReturnContentType(ImageURL)};base64,{Convert.ToBase64String(imageBytes)}");
                    }
                    else
                    {
                        ImageData.Insert(i, "");
                    }
                }

                ImageDataPerFrame[key] = ImageData;
            }
        }
    }
}
