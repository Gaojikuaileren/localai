# Loading Cow Cat icon pack

Source: `../loading-cow-cat-icon-final-bw-polished-v1.png` (`1254 x 1254`).

All exports keep the same crop and composition. Generic PNG, favicon, Android, and Windows assets use transparent rounded corners. Apple, store-listing, thumbnail, and maskable assets use an opaque white background.

## Folder map

| Folder | Intended use |
| --- | --- |
| `png/` | General-purpose square PNGs from 16 to 1024 px |
| `web/` | Favicons, Apple Touch, PWA, maskable, WebP, and Microsoft tile assets |
| `windows/` | Multi-frame desktop/app ICO and common MSIX/UWP square assets |
| `android/` | Android launcher density buckets and Play Store icon |
| `ios/` | iPhone/iPad point-size exports and App Store icon |
| `thumbnails/` | UI thumbnails plus 1200 x 1200 and 1200 x 630 social previews |

`manifest.json` lists every generated file, exact dimensions, purpose, and background policy.

## Web example

```html
<link rel="icon" href="/icons/favicon.ico" sizes="any">
<link rel="icon" type="image/png" sizes="32x32" href="/icons/favicon-32x32.png">
<link rel="apple-touch-icon" sizes="180x180" href="/icons/apple-touch-icon.png">
<link rel="manifest" href="/site.webmanifest">
```

Copy `web/site.webmanifest.example` to the web root and update paths if the icon folder is mounted elsewhere.

## Windows desktop example

The multi-frame icon is `windows/loading-cow-cat.ico`. A .NET project can reference it with:

```xml
<ApplicationIcon>Assets\icon\icon-pack\windows\loading-cow-cat.ico</ApplicationIcon>
```

The current project file was not changed automatically.

## Small-size note

The 16–24 px exports keep antialiasing because forcing strict 1-bit pixels removes parts of the 12-segment loading ring. Use `web/favicon.ico` for browser tabs and `windows/loading-cow-cat.ico` for Windows shells so each platform selects the most suitable embedded frame.
