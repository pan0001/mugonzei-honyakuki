# Mugonzei Honyakuki

**無言勢翻訳機 · 无言势翻译器**

[简体中文](README.md) · [日本語](README.ja.md) · [English](README.en.md)

[Official website](https://mugonzei.top) · [Account center](https://mugonzei.top/account.html)

Translate handwritten or printed text on a Windows desktop or in SteamVR, with subtitles near the bottom of the monitor or headset view.

[Windows x64 installer · v0.2.8-preview](https://github.com/pan0001/mugonzei-honyakuki/releases/download/v0.2.8-preview/Mugonzei-Honyakuki-Setup-win-x64-v0.2.8-preview.exe) · [Portable ZIP](https://github.com/pan0001/mugonzei-honyakuki/releases/download/v0.2.8-preview/Mugonzei-Honyakuki-win-x64-v0.2.8-preview.zip) · [Report an issue](https://github.com/pan0001/mugonzei-honyakuki/issues)

**Example: translating a station sign into Simplified Chinese**

| Screenshot preview | Translation | Recognized text |
| --- | --- | --- |
| A Japanese station sign | 下一班列车将于 10 点到达。 | 次の電車は 10 時に到着します。 |

> Experimental Windows x64 application. Handwriting recognition can misread or omit characters; check the result against the original image. This project is independent of SteamVR, VRChat, and the AI service providers.

## Features

- **Controller capture:** hold the left X button for 1.5 seconds to trigger a capture. A ring below your view shows progress. Continue holding until 3 seconds to cancel the current task and hide the subtitles.
- **Subtitle overlay:** a wide, low-profile translucent bar with pagination, optional source text, and adjustable position, width, and display duration.
- **AI handwriting translation:** send one screenshot to translate Chinese, Japanese or English handwriting directly. With your own AI service, you can also request a separate transcription to check the writing.
- **Direct image analysis:** game screenshots prioritize dialogue, handwriting, signs and quest text. Web pages and documents keep their main text. The translator asks the model to ignore performance overlays, hotkey hints, OSC controls and the taskbar, and collapses duplicate subtitles.
- **Local OCR:** Tesseract for printed text and a separate NDLOCR-Lite engine for Japanese recognition. The recognition stage can run locally.
- **Multiple translation services:** an existing Codex login, cloud or local OpenAI-compatible APIs, and a background Google Translate webpage.
- **Accounts and credits:** top up through Afdian from your account and view your orders and transactions. A complete successful translation costs one credit; your own providers do not consume server credits.
- **Optional region selection:** use both controllers to draw a rectangle, translate only that region, and keep the result fixed in SteamVR tracking space.
- **Save problem samples:** manually save the current screenshot from the workspace. Your own providers do not create translation history; the credit server retains recent results for recovery after a disconnect.

## Requirements

- Windows x64. Desktop mode requires no SteamVR or headset. VR mode requires a compatible headset and controllers.
- Codex mode: an installed Codex CLI with an active login. It calls an online model; inference is not offline.
- Other AI APIs: screenshot translation requires image input. Multiple images per request are only needed for the separate detailed transcription with your own AI service.
- The embedded interface requires the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). It loads from the installed files. Google Translate also requires internet access.
- Both the installer and the portable ZIP include the .NET runtime, Tesseract language models, and the local Japanese OCR runtime. Building from source requires the .NET 10 SDK and an internet connection when preparing dependencies for the first time.

The desktop interface is currently mainly in Simplified Chinese. This repository provides documentation in Chinese, Japanese, and English. Chinese UI labels are retained below so you can find the corresponding controls.

## Quick start

1. Run the Windows installer for your user account, then open **Mugonzei Honyakuki** from the Start menu. For the portable version, extract the entire ZIP to a folder you can write to and run `SteamVrTranslator.exe`.
2. Choose the account credit service, your own API, local Codex, or local OCR with Google Translate.
3. The credit service opens a dedicated sign-in page before mode selection. Your own API, Codex and Google Translate do not require a credit account.
4. Leave the mode on Auto: **Ctrl+Alt+T** captures your primary monitor. To capture an individual app, open 「截图来源」 next to the capture button, select 「指定窗口」, choose the window, and apply. The button and the shortcut use the same source. A closed or minimized target produces a message instead of a screenshot from another window. After SteamVR connects, hold **left X for 1.5 seconds** to translate. Closing SteamVR returns to the desktop. Select Desktop to stay there, or Auto to resume following SteamVR. No mode launches SteamVR.

The workspace has three actions: capture and translate, cancel, and keep the current screenshot. It shows the screenshot, translation and original text read from the image. The Account & AI Services page has three sections in order: Sign in, Preferences, and Credits. Signing in there does not change your translation provider.

Open Account & AI Services → Preferences to change providers or rerun setup. Existing settings and saved keys are preserved.

「检测 Codex 登录」 (Check Codex Login) only checks login status. 「测试字幕」 (Test Subtitles) only displays a fixed example sentence. Use a recognition button to check the source text first; AI recognition buttons still send images. Minimizing keeps the app in the system tray; closing the window exits the app.

If `VCRUNTIME140.dll` or `MSVCP140.dll` is missing, install Microsoft's [Visual C++ x64 Redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe). For installing the runtime required by Google webpage mode, see [Microsoft WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).

## Upgrading and incremental updates

If you use v0.2.5-preview or earlier, download the current version manually from the links at the top of this page. Starting with v0.2.6-preview, select 「检查更新」 (Check for updates) at the bottom of the workspace sidebar. In a narrow window, the button is on the right of the top navigation bar. The app queries GitHub only when you click and shows your installed version and the latest release. If an update is available, select 「下载新版」 (Download update) to download the official installer in your default browser. If the release has no matching installer, select 「查看发布页」 (View release) for other downloads.

Exit the app before upgrading. Run the new installer to update the existing installation. For the portable version, extract the entire new ZIP to a new folder and run `SteamVrTranslator.exe` from there. Both versions reuse the settings and saved keys in `%LOCALAPPDATA%/SteamVrTranslator/`.

To update a Git source checkout, run the following commands in the existing repository, then launch the newly built application:

```powershell
git pull --ff-only
.\build.ps1
```

Further changes are delivered through incremental commits and released under new version tags. Published versions and existing tags are retained.

## Choosing an image recognition mode

| Mode in the UI | Processing flow | Where images go |
| --- | --- | --- |
| 本地OCR（不推荐） (Local OCR, not recommended) | Tesseract recognition, followed by text translation | Recognition stays on your computer; only the source text is sent to the translation service |
| 直接发送截图给模型 (Send Screenshot Directly to Model) | A vision model reads and translates the image, choosing its own output format | The screenshot is sent to the selected Codex / API service |
| 手写截图直译（AI） (AI handwriting translation) | Translate handwriting directly in one model request | One screenshot goes to the selected provider; only the translation is returned |
| 本地日语手写 OCR → 翻译 (Local Japanese Handwriting OCR → Translation) | Local NDLOCR-Lite recognition, followed by text translation | Recognition stays on your computer; only the source text is sent to the translation service |

Capture and Translate sends a single model request in both direct image and AI handwriting modes. For source text alongside a translation, use local OCR. The separate transcription action with your own AI can locate and enlarge writing and may use multiple requests. Enlargement cannot recover missing or obscured strokes.

## Configuring a translation service

### Credit service and Afdian

The public account backend is live. First-time setup selects 「账号积分服务（推荐）」 (Account Credit Service, Recommended) with `https://api.mugonzei.top` already filled in. Step two is for sign-in only; in the workspace, sign in under 「账号与 AI 服务 → 账号登录」 and use 「积分服务 → 打开积分服务」 to check your balance, top up, or redeem a code. You can open your credit account without changing your translation provider. The [website account center](https://mugonzei.top/account.html) uses the same account, balance, and records as the desktop app. Website session tokens remain only in the current page's memory; reloading or closing the page requires signing in again.

New registrations require email verification. On the registration page reached from desktop sign-in or the website’s Create Account tab, enter your email, request a code, and enter the six digits from the message within ten minutes. The email code is separate from a credit redemption code. Existing users keep their balance and sign in with their original username and password. The website and desktop use the same official account server by default. If you select a custom server, its operator stores your account data.

If you host the service with Alibaba Cloud DirectMail, check the primary account’s remaining free allowance and set the [registration email limit](server/deploy/README.md#注册邮件与账户存储). The server sends at most 200 emails in any 24 hours. Once it reaches the lifetime limit, it stops sending codes. Existing users can still sign in.

**The public service offers hosted translation and Afdian top-ups. CNY 5 buys 200 credits and CNY 10 buys 500 credits.** Sign in to the translator account you want to credit, select a plan, and create an order. Follow its payment link to Afdian, select one month, and check the total before paying. Return to your account and select 「核验到账」 on that order to verify the payment and check your balance. The first real payment has yet to be verified through to the account balance. You can also redeem a code from the operator or continue using your own Codex, AI API, or Google Translate service. Sign-in defaults to the official service. Upgrades replace the old default `http://127.0.0.1:5187` if there is no saved session for that address. You can set a self-hosted server under Account & AI Services → Credit Service.

**Payment coupons:** When topping up on the web or desktop, choose a plan, enter the coupon and check the original price, discount and amount due before paying on Afdian. You receive the full number of credits. Entering a coupon alone does not add credits; an invalid or expired coupon never creates a full-price order. This requires an updated checkout and server. If you use desktop v0.2.6 or earlier, upgrade or enter the coupon on the website. [Operator setup](server/COUPONS.md)

If the operator provides a redemption code, enter it in the website account center or the desktop 「积分服务 → 打开积分服务 → 账户」 (Credit Service → Open Credit Service → Account), then click 「兑换」 (Redeem). Both use the same balance for the same account and service. Every account can claim a shared promotion code once. A single-use voucher can be claimed by just one account. Retrying a code you already claimed never adds credits again. The desktop app does not save codes; successful redemptions appear as 「兑换码入账」 in the ledger. Redeeming credits does not change translation service availability; check the status shown in the client.

- One complete successful task costs **one credit**. Image and AI handwriting modes return only the translation in a single model request. Local OCR sends only the recognized text and can display the original alongside the translation.
- A screenshot containing only recognized interface clutter is treated as having no main text. No text, failure, or cancellation before server completion costs nothing. Hiding a completed result does not refund it. Repeatedly querying the same task does not charge again; use the account window to recover the latest task after a disconnect.
- Afdian notifications trigger an authenticated server-side order query before crediting. Orders are credited once; mismatched amount, plan, or account association is rejected.
- Local recognition-only remains free. AI recognition-only is available through your own Codex/API, not the credit service. Existing providers and settings are preserved.
- Desktop session tokens are isolated by server address and protected using Windows DPAPI. Afdian and upstream AI keys stay on the server. Accounts, orders, ledger entries, and recent source/translated text are stored server-side; images are used only during task processing.

To self-host, deploy the server separately and configure Afdian creator/developer access, actual plans, and an AI provider. Live payments are disabled by default. Local mock payments take no money and mock translation calls no real AI. See the [server guide](server/README.md) for deployment, APIs, tests, and refund operations. This implementation uses the official protocol independently; it incorporates no VRCLS or other AGPL project code.

In `/admin/`, operators can search by username or account ID to see a user's balance, reserved credits, orders, and credit history. Adjusting credits, suspending an account, or restoring it requires a reason and creates an audit entry. The panel also shows payment totals and manages discount and redemption codes. [Setup and local demo](server/ADMIN.md)

Operators can choose a custom code when issuing one credit voucher. Redeem it in the [website account center](https://mugonzei.top/account.html) to fund the same account on desktop. Desktop v0.2.6 users can claim custom codes on the website.

See the [deployment guide](server/deploy/README.md) for initial Linux / systemd setup, SSH tunneling, and backups.

### Codex

The app uses the Codex CLI's existing login; you do not need to enter an API key here. Leave the executable path blank for automatic detection. Leave the model blank to use the CLI's default selection. The app does not inherit the model setting from your personal `config.toml` or continue an existing chat.

To use Codex for image recognition, set 「识图方式」 (Image Recognition Mode) to 「直接发送截图给模型」 (Send Screenshot Directly to Model) or 「手写截图直译（AI）」 (Direct AI handwriting translation). To specify a model, you can enter `gpt-6.1-terra`, subject to the models actually available to your account.

「思考程度」 (Reasoning Effort) supports 「自动（按阶段）」 (Automatic by Stage), `low`, `medium`, and `high`. Automatic uses low for direct image analysis and text translation, medium for locating handwriting, and high for reading multiple views in detail. An explicit selection overrides every Codex recognition and translation stage. Save changes before using them. Calls count toward your account usage; account permissions, limits, and network conditions affect availability.

### Cloud or local API

Select 「其他 AI API」 (Other AI API), then enter a Base URL or full `/chat/completions` URL, the model name, and any key required by the service. Chat Completions JSON/SSE responses are supported. Images are sent as PNG data URLs in `image_url` items. In streaming mode, subtitles appear after the full response is received; they do not update character by character.

Use a provider preset or enter a compatible address yourself. The local presets are:

- Ollama: `http://localhost:11434/v1/`
- LM Studio: `http://localhost:1234/v1/`

Start the local service and load a suitable model yourself first. You can leave the key blank if authentication is disabled. This app does not automatically download or start models, and it does not automatically switch to a cloud service on failure. The Codex reasoning effort setting does not affect these APIs.

「测试图片识别」 (Test Image Recognition) sends a fixed image generated by the app, not your current game view. When switching service addresses, enter the model and key for that service again. Unsaved AI settings block new recognition requests.

### Background Google Translate webpage

Select 「Google Translate（后台网页）」 (Google Translate — Background Webpage) together with 「本地OCR（不推荐）」 (Local OCR, not recommended) or local Japanese OCR. The app enters recognized text into a separate WebView2 page and reads the translation. No API key is required. This is neither Gemini nor the Google Cloud Translation API.

The webpage works in the background by default, without using desktop mouse automation to enter text. 「查看翻译网页」 (View Translation Webpage) shows the page. Each request is limited to 5,000 characters. CAPTCHAs, rate limits, access denials, or changes to the webpage may cause translation to fail. The app does not bypass verification or automatically switch services.

## Controls and subtitles

| Action | Default binding |
| --- | --- |
| Capture and translate with Touch / Quest through SteamVR | Hold left X for 1.5 seconds; continue to 3 seconds to cancel |
| Capture and translate with Index | Hold left A for 1.5 seconds; continue to 3 seconds to cancel |
| Capture and translate with Vive | Hold the right menu button for 1.5 seconds; continue to 3 seconds to cancel |
| Change subtitle page with a controller | Touch / Index: click the left thumbstick; Vive: left menu button |
| Capture and translate with the keyboard | `Ctrl+Alt+T` |
| Next page / previous page | `Ctrl+Alt+→` / `Ctrl+Alt+←` |
| Show or hide the last subtitles | `Ctrl+Alt+H` |

If the translation shortcut is already in use, the app tries `Ctrl+Alt+F8`, `Ctrl+Alt+F9`, then `F8`. The active binding is shown at the bottom of the window. Open the SteamVR binding editor from 「截图与按键」 (Capture and Controls) to change controller bindings. The app does not suppress the same button's input in the game.

A new screenshot replaces the previous task. Late results from a canceled task cannot overwrite newer subtitles. The app briefly hides its own overlays while capturing to keep its ring and subtitles out of the screenshot.

## Optional: two-controller region selection

In 「截图与按键」 (Capture and Controls), enable 「双手柄框选」 (Two-Controller Region Selection) and save. Hold left X and point the left and right controllers at opposite corners of a rectangle. **Release X** to capture and translate the selected region. This mode replaces the controller's 1.5-second / 3-second hold behavior. Keyboard and desktop buttons still take normal screenshots.

The frame is fixed in SteamVR tracking space. It does not automatically follow game objects, thumbstick movement, or teleportation, and it has no depth occlusion from the game scene. Fast movement, tracking loss, or older custom action bindings can affect region selection. Turn this option off to restore normal screenshots and subtitles below your view.

## Data and privacy

- Settings are stored in `%LOCALAPPDATA%/SteamVrTranslator/`. API keys are encrypted with DPAPI for the current Windows user. Codex login is managed by the CLI itself.
- Cloud vision models receive screenshots. With local OCR, only the recognized text is sent to the selected translation service. The entire process can be offline only when both recognition and translation use local services.
- Codex and local Japanese OCR create temporary PNG files and attempt to clean them up on completion, failure, or cancellation. Forced termination or a power outage can leave temporary files behind.
- The Google webpage uses a separate `GoogleWebView2/` data directory. It does not share your personal browser login, but it retains website cookies and cache.
- The desktop app does not automatically save screenshots; 「保留当前截图」 (Save Current Screenshot) is a manual action. Your own providers do not save translation history. The credit server retains recent source text and translations for recovery, plus order and credit records. Development diagnostics separately write images, source text, or result reports.
- Redact private information before submitting an issue. Do not upload settings, credentials, personal screenshots, or entire diagnostic directories.

## Building from source

After installing the .NET 10 SDK and Git on Windows x64:

```powershell
git clone https://github.com/pan0001/mugonzei-honyakuki.git
cd mugonzei-honyakuki
.\build.ps1
```

To create a local Windows installer, first run `./tools/package-release.ps1` to verify the portable archive, then run `./tools/package-installer.ps1 -MakeNsis <path-to-makensis.exe>` from PowerShell 7 with NSIS 3. The Setup EXE in `dist/` includes the app and OCR runtime and installs for the current user.

The script prepares missing OCR resources, runs tests, and creates a self-contained release directory at `dist/SteamVrTranslator/`. The local Japanese runtime is installed in a private project directory without changing global Python. NDLOCR-Lite is pinned to 1.3.1. See the preparation scripts and generated runtime manifest for model and dependency sources and verification details.

To run the tests or UI demo separately:

```powershell
dotnet run --project tests/SteamVrTranslator.Tests.csproj -c Release
.\dist\SteamVrTranslator\SteamVrTranslator.exe --demo
```

The regular automated tests use fixed samples and mock services. `--demo` does not automatically connect to SteamVR or call AI. Diagnostics such as `--codex-smoke`, `--vision-smoke`, and `--handwriting-smoke` call real models and may consume account usage; do not treat them as offline tests.

## Known limitations and feedback

Handwriting, small text, perspective, reflections, and complex backgrounds can cause incorrect or missing characters, or include unrelated background text. Tests on development samples do not establish accuracy in real use. SteamVR controller input, headset readability, and service compatibility need to be checked with the actual devices and services. Google webpage mode may stop working when the webpage changes.

When reporting a problem, include the application version, image recognition mode, service name, controller type, and steps to reproduce it. If you choose to provide an image, crop out unrelated or private content first, and include the correct source text and the incorrect result.

## Third-party components

The main components are OpenVR, Vortice.Windows, Tesseract, NDLOCR-Lite, .NET, and WebView2. Each component is subject to its own license. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the license files retained with the source or release package. In particular, NDLOCR-Lite and its model-related components must not be described as having a single shared license.
