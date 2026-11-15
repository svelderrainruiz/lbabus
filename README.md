# lbabus

`lbabus` is the coordination bus CLI of the LabVIEW Benchmark Actor ecosystem: a .NET 8 tool for the `net` TCP/UDP
bus between hosts and reviewer VMs, version-pinned agent instructions (`lbabus agents`), and the documentation
package (`lbabus docs`). The tool's own guide is [tools/collab-cli/README.md](tools/collab-cli/README.md).

## Provenance

This repository was split out of
[svelderrainruiz/labview-benchmark-actor](https://github.com/svelderrainruiz/labview-benchmark-actor) at `develop`
`2b97c69`, with history, so that the extension consumes `lbabus` as a separate ecosystem component.

- The layout is unchanged from the extension repository (`tools/collab-cli/`), so the build paths, embedded
  resources, and Docker harness stages work as before. The extension's requirements-coverage stage (`ci-reqs`)
  and its Vagrantfile checks stayed with the extension. Flattening the layout is a later change.
- `docs/requirements/srs.md` and `rtm.csv` are the extension's requirements, which `lbabus docs show srs|rtm`
  embeds. Until `lbabus` has its own specification they are a snapshot from the extension at the split commit.
- The extension's release-governance files (the release agreement, the reviewer allowlist, and their
  verifiers) stay in the extension repository.
- labview-benchmark-actor 1.5.2 still ships its in-tree copy; its next release consumes a pinned `lbabus`
  release from this repository.

## Build and test

```bash
dotnet build tools/collab-cli/LbaBus.csproj -c Release
docker build -f tools/collab-cli/ci/Dockerfile --target ci-docs .
```

CI runs the Docker harness stages and a native Windows build on every pull request and on pushes to `main` and
`develop`. The repo-standards-review consumer kit pins the standards release this repository follows.

## License

[BSD Zero Clause License](LICENSE).
