# Third-party components

- Valve OpenVR SDK v2.15.6: https://github.com/ValveSoftware/openvr/tree/v2.15.6 — BSD 3-clause, included in licenses/OpenVR-LICENSE.txt.
- Vortice.Windows 3.8.3: https://github.com/amerkoleci/Vortice.Windows — MIT.
- Tesseract .NET wrapper 5.2.0: https://github.com/charlesw/tesseract — Apache 2.0.
- Tesseract OCR and tessdata_fast: https://github.com/tesseract-ocr — Apache 2.0, model license and source hashes in tessdata/.
- Leptonica: http://www.leptonica.org/about-the-license.html — BSD 2-clause.
- .NET 10 Core runtime and Windows Desktop runtime: https://github.com/dotnet — MIT and component-specific third-party terms. The source tree does not contain these runtime packs. After a self-contained publish, `build.ps1` reads the actual runtime versions from the published `.deps.json`, locates their restored packages using `obj/project.assets.json`, and copies the Core runtime's `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` plus the Windows Desktop runtime's `LICENSE` into `licenses/DotNetRuntime-LICENSE.txt`, `licenses/DotNetRuntime-THIRD-PARTY-NOTICES.txt`, and `licenses/WindowsDesktopRuntime-LICENSE.txt`. Package versions and license hashes are recorded in `licenses/DotNetRuntime-manifest.json`. The build fails if these required files cannot be found; this statement does not imply that arbitrary direct `dotnet publish` output already includes them.
- Microsoft Edge WebView2 SDK, `Microsoft.Web.WebView2` **1.0.4314.35**, copyright Microsoft Corporation: BSD 3-Clause; full package license included as `licenses/WebView2-LICENSE.txt` (source copy: `vendor/WebView2-LICENSE.txt`). [Official NuGet package](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35), [version-specific license](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35/License). The Microsoft Edge WebView2 Runtime is a separate Microsoft component with its own terms and must be installed on the user's machine; this application's SDK version does not pin the installed Runtime version. The application does not bundle a fixed Runtime or automatically download/install one. [Microsoft deployment documentation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).

The WebView2 SDK package's additional third-party notices are retained unchanged as `licenses/WebView2-THIRD-PARTY-LICENSE-NOTICES.txt` (source copy under `vendor/`).

Google Translate is accessed as an external website in the embedded WebView2 browser. It is not a bundled translation model, the Gemini API, or the Google Cloud Translation API. This integration does not grant rights to Google's website or change its terms. WebView2 browser data for this application uses a separate user data directory and does not reuse the user's personal browser profile. [Microsoft user data folder documentation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder).

## Optional credit server

The separately built `server/` uses ASP.NET Core / .NET 10 and `Microsoft.Data.Sqlite` / `Microsoft.Data.Sqlite.Core` 10.0.0 (MIT). It also uses SQLitePCLRaw managed components 2.1.11 (Apache-2.0), with the native SQLite package explicitly pinned to `SQLitePCLRaw.lib.e_sqlite3` 3.53.3 (SQLite public domain). Restored dependency versions are recorded in the server's NuGet assets and published `.deps.json`. These components are not added to the desktop application.

The server retains and publishes license texts under `server/licenses/`: [Microsoft.Data.Sqlite upstream v10.0.0 license](https://github.com/dotnet/efcore/blob/v10.0.0/LICENSE.txt), [SQLitePCLRaw upstream v2.1.11 license](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.11/LICENSE.TXT), and the native 3.53.3 package's `LICENSE.txt` describing [SQLite's public-domain status](https://sqlite.org/copyright.html). Afdian is an external platform accessed through its [official developer API](https://guide.afdian.com/creator/developer); no Afdian service, merchant credentials, or payment approval is bundled. The account/payment/credit implementation is original project code, not copied from VRCLS or AGPL integration projects. This notice does not add a license for the project's original source.

## Local Japanese OCR runtime

The `local-ocr-runtime/` directory contains the separately licensed local inference runtime. Its `runtime-manifest.json` records the exact Python and dependency versions, source URLs, and model SHA256 hashes used in this distribution. Do not remove the bundled licenses when copying or distributing that directory.

- **NDLOCR-Lite**, by the **National Diet Library, Japan (`ndl-lab`)**: release tag **1.3.1**, commit **`6feccb29e33c2467ea48894e66502a7788ae2b2d`**. [Pinned source](https://github.com/ndl-lab/ndlocr-lite/tree/6feccb29e33c2467ea48894e66502a7788ae2b2d). The upstream project is provided under **Creative Commons Attribution 4.0 International**, with separate dependency terms. The original files are retained as `local-ocr-runtime/ndlocr-lite/LICENCE` and `local-ocr-runtime/ndlocr-lite/LICENCE_DEPENDENCEIES` (the latter spelling is from upstream). This application adds a headless worker/process adapter; it is not an official NDL application or an endorsement by NDL.
- **DINOv3**, by **Meta**: the detector uses a DINOv3-distilled backbone. The model-related **DINOv3 License** is included separately as `local-ocr-runtime/licenses/DINOv3-LICENSE.md`; it must not be replaced by, or described solely as, CC BY 4.0. [Pinned upstream license](https://github.com/facebookresearch/dinov3/blob/6876159a11b4df116f30f667f8c9888617df0751/LICENSE.md).
- **CPython 3.13.16 Windows embedded distribution**: Python Software Foundation License and the additional notices included in `local-ocr-runtime/python/LICENSE.txt`, also copied to `local-ocr-runtime/licenses/Python-LICENSE.txt`. [Python licensing](https://docs.python.org/3/license.html).
- **ONNX Runtime 1.26.0**: MIT; **OpenCV headless 4.11.0.86**: Apache 2.0 plus bundled component notices; **NumPy 2.2.2**: BSD 3-clause plus bundled component notices; **Pillow 12.1.1**: HPND. The package licenses are retained in the embedded Python's `Lib/site-packages/` package and `.dist-info` directories.
- The runtime also includes supporting packages such as lxml, NetworkX, PyYAML, protobuf, FlatBuffers, packaging, tqdm and Colorama. Their own licenses and notices remain in their package or `.dist-info` directories. The runtime manifest, rather than the upstream GUI dependency list, identifies the headless dependencies actually bundled here.

NDLOCR-Lite's original source and pretrained model files are used for CPU recognition; the integration does not retrain the models. The runtime's local operation does not change any upstream license or extend its language/accuracy guarantees.

Official implementation references:
- OpenVR actions: https://github.com/ValveSoftware/openvr/wiki/Action-manifest
- OpenVR interfaces: https://github.com/ValveSoftware/openvr/blob/v2.15.6/headers/openvr.h
- Chat Completions compatible schema: https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create

This software is an independent application and is not affiliated with Valve, the National Diet Library, Meta, Microsoft, Google, or any AI service provider.
