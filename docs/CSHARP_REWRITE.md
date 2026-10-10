# C# application architecture

ReAnimated is a Windows WPF application for Dying Light 1 animation and model authoring.

| Project | Responsibility |
| --- | --- |
| Core | Animation, model and project contracts; package persistence |
| Codecs | ANM2, animation scripts, FBX and RPack readers and writers |
| Evaluation | Sampling, retargeting, authoring layers and event preview state |
| DL1.Assets | Installation discovery and asset catalogs |
| Retargeting | Rig correspondence and IK |
| Renderer.D3D11 | Viewport rendering |
| App | Editor workspaces and commands |
| Cli | Command-line dispatch and package operations |

Animation clips contain sampled motion. Sequence uses contain ranges, playback rates and events. Each sequence can reference the same animation with different event timing. Script source and compiled event rows retain their respective backing data.

Projects use schema 4 and model packages use schema 10. Readers migrate previous supported versions. Event changes participate in document history, saving and recovery. Timeline positions accept fractions; source-frame adapters serve existing authoring operations.

Build with `build_csharp.ps1`. Run `tools/validate_csharp.ps1` for focused or hermetic validation. Packaged application checks are described in `DL1_WPF_STARTUP_ACCEPTANCE.md`.
