# Publishing to the Microsoft Store

DragDone ships through two channels from one codebase:

| Channel | Artifact | Who signs it | Startup path |
| --- | --- | --- | --- |
| GitHub Releases | `DragDone-<version>-setup.exe` (Inno Setup, self-contained) | nobody — users see SmartScreen once | `HKCU\...\Run` |
| Microsoft Store | `DragDone-<version>-x64.msix` | Microsoft, during publishing | a Store startup task |

The executable is the same. It asks Windows whether it has package identity and picks the matching
startup mechanism, so nothing has to be built twice or configured per channel.

## Building the package

```powershell
pwsh tools/build_msix.ps1            # unsigned, which is what Partner Center wants
pwsh tools/build_msix.ps1 -Sign      # self-signed, for installing on your own machine
```

The script publishes self-contained `win-x64`, stages the layout with `AppxManifest.xml` and the
tile assets, fills the manifest placeholders from `packaging/identity.json` and the project version,
and packs it with `MakeAppx` from the Windows SDK (any version under
`%ProgramFiles(x86)%\Windows Kits\10\bin`).

## Before you can submit

0. **Open an individual developer account — it is free.** Individual registration no longer carries
   the old $19 fee: go to <https://storedeveloper.microsoft.com>, choose *Individual developer
   (free)*, sign in with the Microsoft account you intend to keep, verify your identity with a
   government ID plus a selfie on a phone, and you land in Partner Center. A Company account costs
   money and is only needed if you are publishing as a business.

1. **Reserve the name in Partner Center.** Apps and games → **New product** → **MSIX or PWA app**
   (not *Game*). Check availability for `DragDone`, then **Reserve product name**. An unused
   reservation expires after three months.

2. **Copy the three identity values** from the product's **Product identity** page and put them in
   `packaging/identity.json`:

   ```json
   {
     "identityName": "<Package/Identity/Name from Partner Center>",
     "publisher": "<Package/Identity/Publisher from Partner Center>",
     "publisherDisplayName": "<Publisher display name>"
   }
   ```

   The values in that file are the ones Partner Center assigned to this product
   (`trookish.DragDone`, `CN=9121850F-2302-4F5F-8644-8CDC73A97BA8`). The manifest's `Name` must
   equal the reserved identity name exactly, and `Publisher` must equal the `CN=` value character
   for character — that is the certificate Partner Center issues when it signs your package. If the
   product is ever moved to a Company account, these three values change and the package has to be
   rebuilt.

3. **Keep the version in step with `DragDone.csproj`.** The manifest version is generated from it as
   a four-part number (`1.1.0` becomes `1.1.0.0`). A submission must have a higher version than the
   package already in the Store.

4. **Upload the unsigned `.msix`.** Partner Center accepts a single-architecture `.msix` directly
   (`.msixupload` is only the container Visual Studio builds around a bundle plus symbols) and
   re-signs the package for distribution. Submit the `msix` artifact CI attached to the release, not
   your local build, so the Store copy matches the tagged source.

5. **Have one desktop screenshot at 1366×768 or larger.** PNG, landscape or portrait, up to 10 of
   them; the repo's README captures are smaller than that and cannot be used as-is.

## Verifying a package before submitting

```powershell
pwsh packaging/verify_msix.ps1                 # non-elevated attempts, then layout registration
Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass',`
  '-File','packaging/verify_msix_elevated.ps1' # full install test, self-cleaning
```

The elevated script does the whole end-to-end pass and then removes everything it created:

- trusts the dev certificate for the local machine (signature validation is machine-wide),
- lifts the Appx sideloading policy if the machine has it switched off, and restores it afterwards,
- installs the `.msix`,
- launches the app with no arguments, as Windows would, and checks the process appears,
- reports whether settings resolve to the plain `%APPDATA%\DragDone` or a redirected package folder,
- confirms no `Run` key was written,
- runs `--identity` from the installed executable and expects `packaged = True`, the package family
  name, and `startup = startupTask=Enabled`,
- runs a real FFmpeg job (`vid.mute`) inside the package,
- uninstalls, confirms the package and its redirected data are gone, and removes the dev certificate.

This machine carries `HKLM\SOFTWARE\Policies\Microsoft\Windows\Appx\AllowAllTrustedApps = 0`, which
blocks every sideloaded MSIX. That is exactly why the Store channel matters: Store installs are not
subject to it, and the delivered package is signed by Microsoft rather than by a self-signed
certificate nobody trusts.

## Store listing material

**Age rating:** no user-generated content, no chat, no purchases, no location, no data collection —
suitable for everyone.

**Category:** Utilities & tools / Productivity.

**Privacy policy URL:** `https://github.com/trookish/DragDone/blob/main/PRIVACY.md`.

**Short description (English):**

> Drag a file to your screen edge, pick an action, done. Convert, compress, resize, split PDFs,
> rotate video — all on your PC, nothing uploaded, originals never overwritten.

**Short description (Arabic):**

> اسحب الملف إلى حافة الشاشة واختر الإجراء. تحويل وضغط وتغيير مقاس وتقسيم PDF وتدوير الفيديو، كل ذلك
> على جهازك دون رفع أي شيء، ودون المساس بالملف الأصلي.

**Certification notes** — send these with the submission, because the behaviour looks unusual from
the outside:

> DragDone is a tray application with no main window. It stays in the notification area and reacts
> only when the user drags a file to a screen edge and releases it: a radial menu of actions appears
> under the pointer. It uses a low-level mouse hook purely to notice the start of a shell drag, and a
> non-activating window for the menu so the drag operation is not disturbed. No keyboard or content
> logging, no network access, no telemetry. Files are read only for the action the user picks, and
> results are written next to the original without overwriting it. The first launch shows a short
> welcome window explaining the tray behaviour, since the app would otherwise be invisible.
> Video and audio actions require a locally installed FFmpeg; the app never downloads it and shows a
> localized explanation when it is missing.

**Screenshots:** `docs/images/en-settings.png`, `docs/images/ar-settings.png`, plus the tray menu and
a radial menu capture. Store requires at least one desktop screenshot at 1366×768 or larger.

## What the Store package still depends on

- **FFmpeg** is not bundled. Store users without it get the localized dependency message. Bundling
  would mean shipping an LGPL/GPL build and disclosing its licence in the Store listing — a separate
  decision, not a packaging detail.
- **Microsoft Office** is used only when installed, for Office-specific actions.
- **.NET** is not required: the package is self-contained.
