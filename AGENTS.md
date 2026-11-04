# Project maintenance

- Keep updates incremental in the existing `pan0001/mugonzei-honyakuki` repository. After completing and validating requested changes, commit and push the relevant changes; preserve existing history and release tags. Do not recreate the repository or force-push. Respect any later user instruction to keep a change local.
- Keep the Chinese, Japanese and English README files consistent when changing user-facing setup or operation. The product names are 无言势翻译器 / 無言勢翻訳機 / Mugonzei Honyakuki.
- Preserve existing settings and Windows-protected API keys on upgrades. Never commit user settings, credentials, screenshots, logs, browser profiles or generated release artifacts.
- For behavior changes, run the relevant regression checks and the full `tests/SteamVrTranslator.Tests.csproj` suite before publishing. Build release files in a separate publish directory if the installed app is running.
- Release binaries under a new version and tag; keep older downloads available. `tools/package-release.ps1` derives its default archive version from the project file.
- The owner has chosen to publish the source without an original-source license for now. Do not add one without a new instruction; retain all third-party notices.
