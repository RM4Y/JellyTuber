# Distributing JellyTuber as a one-click Jellyfin repository

Once set up, you (and anyone you share with) install and update the plugin from
inside Jellyfin — no more manual `dotnet publish` / copying DLLs.

## How it works

A Jellyfin **repository** is just a public `manifest.json` URL. It lists plugin
versions, each pointing to a downloadable zip (the compiled DLL + meta.json) and
its MD5 checksum. You add the URL once in Jellyfin; new versions then appear in
the catalog automatically.

## One-time setup

1. Create a **public GitHub repository** and push this project to it.
2. Make sure `manifest.json` is at the repo root (it is, in this project).
3. Your repository URL for Jellyfin will be:
   ```
   https://raw.githubusercontent.com/<you>/<repo>/main/manifest.json
   ```

## Releasing a new version (automated — recommended)

The included GitHub Actions workflow (`.github/workflows/release.yml`) does
everything on a tag push:

```bash
# bump the version in meta.json first, then:
git tag v1.5.0
git push origin v1.5.0
```

GitHub then builds the DLL, packages `jellytuber-1.5.0.0.zip`, computes its
checksum, creates a Release with the zip attached, and commits the updated
`manifest.json`. Within a few minutes the new version shows up in Jellyfin.

> The workflow needs no secrets — it uses the built-in `GITHUB_TOKEN`. Ensure
> Settings → Actions → General → Workflow permissions is set to
> "Read and write permissions".

## Releasing a new version (manual alternative)

If you'd rather not use CI:

```bash
REPO="<you>/<repo>" ./build-release.sh 1.5.0.0
gh release create v1.5.0.0 jellytuber-1.5.0.0.zip --notes "Release 1.5.0.0"
git add manifest.json && git commit -m "release 1.5.0.0" && git push
```

## Installing in Jellyfin

1. **Dashboard → Plugins → Repositories → +**
2. Name: `JellyTuber`, URL: your `raw.githubusercontent.com/.../manifest.json`
3. **Dashboard → Plugins → Catalog** → find **JellyTuber** → Install
4. Restart Jellyfin.

Updates: when you publish a new version, Jellyfin shows an update badge in the
Catalog — one click to upgrade.

## Notes

- `targetAbi` in each version must match the Jellyfin it's built against
  (10.11.6.0 here). Keep it in sync with the `<PackageReference>` versions in
  the `.csproj` if you move to a different Jellyfin release.
- The zip must contain the DLL and `meta.json` at its root (the scripts do this).
- The checksum is the zip's MD5; a mismatch makes Jellyfin refuse the install.
