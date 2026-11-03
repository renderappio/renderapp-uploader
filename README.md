# Renderapp uploader

A desktop app for Windows, Linux and macOS that publishes renders to [Renderapp](https://renderapp.io).
Drop a folder of renders or a `.rio` file onto the window. The app works out the options, spins and views from your file and folder names, shows what it found, and publishes with **Publish to Renderapp**.

Publishing works like this:

1. **Sign-in.** It signs you in through your browser, using OAuth 2.0 with PKCE and a loopback redirect. Renderapp asks you to allow the app, and to choose a studio if you are in several.
2. **Destination.** Renderapp's own page opens so you can choose the project to publish into, or make a new one.
3. **Upload.** The app uploads the base files only, and Renderapp makes every rendition. Uploads go straight to storage through requests Renderapp signs, with no storage credentials on your machine.
4. **Resume.** An interrupted publish carries on where it stopped the next time you publish the same folder.

## Download

Each [release](https://github.com/renderappio/renderapp-uploader/releases/latest) has a download for Windows, Linux and macOS, on both x64 and arm64. None of them needs .NET installed.

- **Windows:** unzip and run `renderapp-uploader.exe`. The app isn't code-signed yet, so SmartScreen may ask you to confirm the first time.
- **Linux:** extract the `.tar.gz` and run `renderapp-uploader`.
- **macOS:** unzip and move **Renderapp Uploader** to Applications. It isn't notarised yet, so macOS blocks it the first time. Try to open it once, then choose **Open Anyway** under System Settings, Privacy & Security.

`SHA256SUMS` in each release lists the checksum of every download.

## Releasing

Push a tag such as `v1.2.0`, and the release workflow tests and packages every platform, then publishes the release with generated notes. A tag with a suffix, such as `v1.2.0-beta.1`, is published as a pre-release. To build the downloads without releasing, run the workflow by hand from the Actions tab.

## Build and run

Needs a [.NET 10 SDK](https://dotnet.microsoft.com/download), 10.0.100 or a newer 10.0 release, as `global.json` pins.

```sh
dotnet run --project src/Renderapp.Uploader
```

The tests run the real window on Avalonia's headless platform, so they need no display:

```sh
dotnet test
```

To build a self-contained app for one platform, pick its runtime identifier, such as `win-x64`, `linux-x64` or `osx-arm64`:

```sh
dotnet publish src/Renderapp.Uploader -c Release -r linux-x64 --self-contained
```

Set `RENDERAPP_URL` to point the app at another Renderapp instance, for example a local server.
