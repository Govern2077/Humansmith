# Humansmith

Unity version: 2022.3.8f1c1

## Getting started

Install Git LFS, then clone this repository and open the project with Unity Hub:

```powershell
git lfs install
git clone https://github.com/Govern2077/Humansmith.git
```

The MediaPipe archive in Packages is stored with Git LFS. Keep Assets (including .meta files), Packages, and ProjectSettings in version control.

## Version management

- Commit each completed, verified change with a short Chinese description.
- Include the upload time in the commit message using Asia/Shanghai time: `YYYY-MM-DD HH:mm +08:00`.
- Push completed commits to origin/main and verify the remote commit matches the local commit.
- For larger changes, use a feature branch before merging into main.
- Exclude Unity caches, local tools, credentials, and generated builds.
- Uploads run during active project work; no background synchronization is configured.
