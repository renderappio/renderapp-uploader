# Renderapp uploader

A desktop app for Windows, Linux and macOS that publishes renders to [Renderapp](https://renderapp.io).
Drop a folder of renders or a `.rio` file onto the window. The app works out the options, spins and views from your file and folder names, shows what it found, and publishes with **Publish to Renderapp**.

Publishing works like this:

1. **Sign-in.** It signs you in through your browser, using OAuth 2.0 with PKCE and a loopback redirect. Renderapp asks you to allow the app, and to choose a studio if you are in several.
2. **Destination.** Renderapp's own page opens so you can choose the project to publish into, or make a new one.
3. **Upload.** The app uploads the base files only, and Renderapp makes every rendition. Uploads go straight to storage through requests Renderapp signs, with no storage credentials on your machine.
4. **Resume.** An interrupted publish carries on where it stopped the next time you publish the same folder.

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
