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

## Mouth-width marking prototype

Open Assets/Scenes/Facetest.unity in Play Mode. Select the right-side 术前标记 tab, set the width ratio, then click 生成术前标记. F9 generates/applies the target; F10 restores the captured face and resumes paused drivers. This is a reversible preview, not a permanent rest-pose edit. The left tabs select measurements, tools, and expressions; the right tabs select markings, photo fitting, and camera capture.

Marking appearance: black registration ticks, a dashed target curve, and direction arrows. Default line width is 6 screen pixels (adjustable 3–12); optional pale outlines improve contrast. The original mouth curve can be toggled separately. The dashed target is a game-specific design guide, not a clinical incision plan. No anatomical risk or excess-tissue annotations are inferred from mouth-width requirements.

Visual reference: Adriana Pozzi, “How I Mark My Patients Before Surgery,” ISAPS News, Volume 13, Number 1 (2019), page 60: https://www.isaps.org/media/055cwygk/isap_news_131-2019.pdf . The author describes black main references, red anatomical warnings/depressions, and green excess subcutaneous tissue. This is an individual marking system, not a universal color standard or a validated mouth-width surgical protocol.
