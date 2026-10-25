# nuget-local

Packages the build needs that are not on nuget.org.

| Package | SHA-256 |
|---|---|
| `ryujinx.libhac.0.21.0-alpha.133.nupkg` | `554bf2315368f193335c0ac60d73d73fb8c318aaf3f8a13bddf4ef30e160b631` |

`Ryujinx.LibHac` pre-releases are only published on Ryubing's own registry
(`https://git.ryujinx.app/api/packages/projects/nuget/...`). This copy came from a Nix binary cache of that
download, and its hash matches the one pinned by the Nix packages that fetch it from Ryubing directly
(`sha256-VUvyMVNo8ZMzXArGDXPXP7jDGKrz+KE73fTvMOFgtjE=`).

When `Directory.Packages.props` moves to a newer LibHac, drop the new `.nupkg` here and check its hash the same way.
