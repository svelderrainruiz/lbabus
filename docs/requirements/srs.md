# lbabus Software Requirements

These are the requirements of the LabVIEW Benchmark Actor (LBA) system that are allocated to `lbabus`: the
non-superseded requirements whose code references include a file of lbabus's code (`tools/collab-cli/`). Each keeps its LBA ID and
LBA's text verbatim, from [svelderrainruiz/labview-benchmark-actor](https://github.com/svelderrainruiz/labview-benchmark-actor/blob/3534e4b/docs/requirements/srs.md) at `3534e4b`.

- LBA's [specification](https://github.com/svelderrainruiz/labview-benchmark-actor/blob/3534e4b/docs/requirements/srs.md) and [traceability matrix](https://github.com/svelderrainruiz/labview-benchmark-actor/blob/3534e4b/docs/requirements/rtm.csv) are the source of truth for the whole
  system. This file is lbabus's slice of them; [rtm.csv](rtm.csv) lists the same requirements with their code
  references limited to lbabus's own.
- `lbabus docs show srs|rtm` embeds these two files.
- The governed pages are in the [lbabus wiki](https://github.com/svelderrainruiz/lbabus/wiki).

---

### LBA-REQ-012: Version-pinned agent base instructions

- Status: Proposed
- Area: Agentic infrastructure (extends LBA-REQ-001, LBA-REQ-003)
- Statement: The system shall embed a canonical agent base-instructions document
  (`AGENTS.md`) in the `lbabus` binary and expose it via `lbabus agents`
  (print / `--out <path>` / `--check <path>`), so that every session using a
  given `lbabus` version shares byte-identical base instructions that can be
  hardened version-over-version.
- Acceptance Criteria:
  - The instructions are embedded in the versioned binary; `lbabus agents`
    prints them with a `sha256`-stamped, version-tagged header.
  - `--out <path>` materializes them to a known file location; `--check <path>`
    exits non-zero when a local copy has drifted from the embedded canonical.
  - The `ci-agents` release-harness stage gates the embed round-trip and the
    drift detection, so every published version's instructions are verified.
- Change Guidance: Iterate the source in `tools/collab-cli/agents/AGENTS.md` and
  cut a new release; do not hand-edit materialized copies.

---

### LBA-REQ-059: Host<->VM-agent closed loop over the lbabus net TCP bus

- Status: Proven
- Area: Deployment / agentic (ADR-0039 -- host<->VM-agent closed loop over TCP, off GitHub Discussions)
- Statement: The system shall close the host<->VM-agent coordination loop over the `lbabus net` TCP bus --
  after driving the reviewer VM's Copilot agent, the host awaits the agent's reply frame correlated by task
  id (fail-closed on mismatch/timeout), and the signed reviewer verdict announces with a semantic net type
  (RESOLVED/REFINE/BLOCKED) -- so a fail-closed gate proves the read-back + the semantic types are correctly
  derived and coordination rides TCP, not a GitHub Discussion.
- Rationale: `drive-agent-chat.sh` drove the VM's chat FIRE-AND-SCREENSHOT (a human read PNGs); there was no
  programmatic read-back, so the host agent could not close the loop. The verdict announcement (LBA-REQ-058)
  rode a GitHub Discussion; an operator directive ("TCP, deprecate the use of github discussions") moves
  coordination onto the private TCP bus that already exists (`lbabus net`, LBA-REQ-007, ADR-0003/0004).
- Acceptance Criteria:
  - `await-agent-reply.mjs` runs `lbabus net listen`, parses the rendered frame, and returns the VM agent's
    reply CORRELATED by `--task` + `--type`; it FAILS CLOSED on a task mismatch or a timeout (never accepts an
    uncorrelated frame as the answer).
  - `drive-agent-closed-loop.sh` composes the two halves: it starts the awaiter, then keyboard-injects the
    prompt PLUS a deterministic report-back line (single-line; a newline would submit early) so the VM agent
    replies over TCP.
  - The `net` envelope type set carries RESOLVED/REFINE/BLOCKED (option A), so a signed verdict announces over
    `net` as a first-class semantic event (pass->RESOLVED), preserving ADR-0038's semantics off the Discussion bus.
  - Comms-only holds (ADR-0003): the reply/announcement is a one-line status only, never run data.
- Change Guidance: `await-agent-reply.mjs` (+ `.selftest.mjs`), `drive-agent-closed-loop.sh`, and
  `closed-loop-readback-proof.sh` live under `reviewer-workstation/`; the net type set is `BusWire.Types` in
  `tools/collab-cli/Net.cs`. The FULL retirement of the GitHub-Discussion transport is deferred (ADR-0039
  Consequences). Authored under the singular-requirement directive (one `shall`).

---

### LBA-REQ-060: Live-only net coordination -- the receive-log + net poll read side

- Status: Proven
- Area: Deployment / agentic (ADR-0040 -- live-only net coordination, off GitHub Discussions)
- Statement: The system shall provide a live-only net coordination read side -- a per-actor local receive-log
  written by `lbabus net listen --log` and read by `lbabus net poll` (filtered by type/task; fail-closed
  without a log) -- so a fail-closed gate proves post->log->poll round-trips over TCP and coordination reads no
  longer depend on a GitHub Discussion.
- Rationale: ADR-0039 moved the host<->VM-agent loop + the reviewer-verdict announcement onto `net` TCP; an
  operator directive ("TCP, deprecate the use of github discussions") moves the rest of coordination off
  Discussions. A Discussion did two jobs -- live relay + async persistence; `net send`/`listen` already cover
  the live relay, but the READ side (poll) had no net equivalent. The operator chose a LIVE-ONLY model (no
  central/async store): each actor logs what it hears while online.
- Acceptance Criteria:
  - `net listen --log <file>` appends every received frame to a per-actor JSONL receive-log (BusWire.ToJson),
    best-effort (a log error never breaks the listener).
  - `net poll [--log <file>] [--tail N] [--type T] [--task T]` reads + filters the local receive-log
    (BusWire.FromJson), mirroring the Discussion `poll` UX; with no log it prints nothing + exits 0; with no
    `--log`/`COLLAB_NET_LOG` it FAILS CLOSED.
  - Comms-only holds (ADR-0003): the receive-log stores only small coordination frames, never run data.
  - Accepted tradeoff: a peer offline at post time misses the frame -- no async catch-up.
- Change Guidance: `CmdListen --log` + `CmdPoll` + `BusWire.ToJson`/`FromJson` live in
  `tools/collab-cli/Net.cs`; the loopback proof is `experiments/net-coordination/net-coordination-log-proof.sh`
  (committed receipt `net-coordination-log-receipt.json`). This is the FIRST increment of retiring the
  GitHub-Discussion transport (ADR-0040); the call-site migrations + removal are deferred. Authored under the
  singular-requirement directive (one `shall`).

---

### LBA-REQ-065: Flip the coordination default to net + graceful no-op when unconfigured

- Status: Proven
- Area: Deployment / agentic (ADR-0045 -- flip the coordination default to net, off GitHub Discussions step 6)
- Statement: The system shall default the coordination-bus transport to the live-only `lbabus net` TCP bus
  (GitHub Discussion becomes a legacy opt-out) AND degrade gracefully when net is unconfigured -- `net poll`
  with no receive-log and `net send --skip-if-no-peer` with no peer both exit 0 with a hint (no error, no dead
  loopback) -- so a fresh install coordinates over TCP once a peer/log is set and does nothing quietly until then.
- Rationale: off-Discussions steps 1-5 (ADR-0040..0044) made `net` available in the extension, the MCP surface,
  and post-verdict.mjs, and dropped the release-CI Discussion announce -- but each kept Discussion the DEFAULT
  (opt-in net) during the transition. With the net loop proven live (ADR-0039) + no CI Discussion use, the only
  thing pinning Discussion as the default is inertia. Flipping naively would error/hang an unconfigured install
  (net poll no-log was fail-closed; net send no-peer sat in a dead loopback), so the flip is paired with a
  graceful no-op.
- Acceptance Criteria:
  - `labviewBenchmarkActor.busTransport` defaults to `net`; the extension, the MCP provider + stdio server, and
    post-verdict.mjs all default to `net`; Discussion is the legacy opt-out (`busTransport: "discussion"` /
    `COLLAB_TRANSPORT=discussion`).
  - `net poll` with no receive-log exits 0 with a hint (softened from fail-closed); `net send --skip-if-no-peer`
    with no peer exits 0 with a hint; the extension/MCP/post-verdict callers pass `--skip-if-no-peer` when no
    host is configured -- an unconfigured net-default install is a silent no-op.
  - `npm test` proves the extension + MCP default flip; the net-coordination-log receipt proves the graceful
    poll (`pollWithoutLogGraceful`).
- Change Guidance: the flip is in package.json + src/extension.ts + src/mcp/* + reviewer-workstation/post-verdict.mjs;
  the graceful branches are in tools/collab-cli/Net.cs. Gates `net-default-graceful` + `bus-transport-select`
  (default === 'net'). Updates the ADR-0041/0042/0043 defaults + softens the ADR-0040 poll fail-closed. The FINAL
  step -- deprecating + removing the Discussion transport (Program.cs + GraphQL) + the CI mock GraphQL harness --
  is deferred (ADR-0045 Consequences). Authored under the singular-requirement directive (one `shall`).

---

### LBA-REQ-067: Remove the GitHub-Discussion transport from the lbabus CLI

- Status: Proven
- Area: Deployment / agentic (ADR-0047 -- remove the CLI Discussion transport, off GitHub Discussions step 8 / final)
- Statement: The system shall NOT expose a GitHub-Discussion coordination transport from the `lbabus` CLI -- the
  `init`/`post`/`poll`/`wait`/`delta` subcommands and the GraphQL Discussion client are removed (GitHubGraphQL
  keeps only the REST release-tag + issue-comment calls for `selfcheck`/`defect`), leaving the live-only
  `lbabus net` TCP bus as the sole coordination transport -- so a fail-closed gate proves the CLI carries no
  Discussion transport.
- Rationale: step 7 (ADR-0046) made the coordination product surface net-only, leaving the CLI's own Discussion
  commands (init/post/poll/wait/delta) reachable by nothing in the product. This final step removes them + the
  GraphQL Discussion client, completing the off-Discussions migration (steps 1-8). GitHubGraphQL.cs was shared:
  selfcheck reads release tags (REST) + defect appends an issue comment (REST) -- those keepers stay.
- Acceptance Criteria:
  - `Program.cs` no longer dispatches `init`/`post`/`poll`/`wait`/`delta` (the `Cmd*` methods + help entries +
    the `EnforceVersionOrNull` guard + the `ParseAll`/`SeedBody`/`Eq`/`Dur` helpers are gone); `version`/
    `capabilities`/`selfcheck`/`grep`/`defect`/`net`/`resource`/`agents`/`docs` remain.
  - `GitHubGraphQL.cs` is REST-only (drops the Discussion records + `Query`/`ResolveContext`/`FindDiscussion`/
    `CreateDiscussion`/`EnsureDiscussion`/`ListComments`/`AddComment`; keeps `ListReleaseTags` + `AddIssueComment`);
    `Config.cs` drops the discussion-only fields (`Category`/`Title`/`AgentId`/`Counterpart`/`AddressesMe`).
  - The 12 discussion / version-guard ci cases are retired (the grep + defect + runner-meta cases remain); the
    build + a CLI smoke test verify the removal.
- Change Guidance: the removal is in tools/collab-cli/{Program.cs, GitHubGraphQL.cs, Config.cs} + the ci cases;
  gate `cli-no-discussion-transport`. A doc/cleanup follow-up (step 8b) sweeps the stale docs (collab-cli
  README/AGENTS.md, ci/README.md, docs/mcp-tools.md, reviewer-manual-test-plan.md, root README.md) + trims the
  ci mock's vestigial GraphQL/release handlers + retires experiments/ollama-bus/bus-agent.mjs -- none block a
  gate. Completes the off-Discussions migration. Authored under the singular-requirement directive (one `shall`).

---

### LBA-REQ-095: System-level SemVer, task chronology, and canonical distribution

- Status: Proven
- Area: Configuration management / operation / release (ADR-0078)
- Statement: The system shall bind extension, AGENTS, `lbabus`, and human-task SemVer to one release definition,
  emit deterministic noninteractive task receipts, and admit a stable Marketplace release only after its bytes match
  the canonical GitHub Release, so review chronology and released system identity cannot drift.
- Rationale: Signed 1.4.1 review rejected confusing task capture/reuse interaction, output without an event index or
  named clock, internal component versions that did not force an extension-version change, and Marketplace-first
  publication.
- Acceptance Criteria:
  - `release-components.json` binds the extension, AGENTS, `lbabus`, and human-task SemVer values.
  - The same definition pins the exact `repo-standards-review` source commit and workbench image digest.
  - A repository precommit hook rejects a governed component edit unless that component and the extension versions
   advance and final staged package metadata plus CHANGELOG agree.
  - Human tasks ignore stdin, use dedicated terminals without reuse prompts, and retain indexed JSON events carrying
   UTC wall time, monotonic nanoseconds, and the `process.hrtime.bigint` clock-source name.
  - Extension commands, MCP, and human tasks resolve the stable reviewer `lbabus` installation even when the running
   VS Code/Explorer process inherited PATH before staging.
  - Reviewer staging provisions the pinned Git/ripgrep/GitHub CLI/GitLab CLI/.NET toolchain and requires a green
   `lbabus selfcheck` before presenting the candidate.
  - Windows human tasks invoke the npm command shim, and signing readiness accepts only a key valid for the requested
   release version and quorum purpose.
  - Reviewer verdict validation and the visual gate require a 40-hex source commit and 64-hex VSIX SHA-256.
  - `release-cut-github` explicitly targets `main`.
  - Marketplace publication is opt-in after the canonical GitHub Release exists, verifies the release VSIX hash
   equals the staged VSIX hash, runs from the release tag, and uses stable `vsce publish` on the downloaded release
   asset.
  - Deterministic tests and a local gate reject version, task-receipt, release-target, and publication-order drift.

---

### LBA-REQ-098: Signed autonomous actor protocol

- Status: Proven
- Area: Deployment / security / operation (ADR-0081)
- Statement: The system shall gate autonomous actor benchmark execution through an enrolled-signer protocol that
  binds freshness, target plane, allowlisted workload, exact candidate identity, replay state, plus an actor-signed
  outcome to one request digest.
- Rationale: Existing live mesh runners depend on host SSH or hypervisor control. A production actor must decide
  locally whether one bus-delivered task is authorized, current, intended for its plane, and bound to exact code.
- Acceptance Criteria:
  - Requests carry an enrolled requester signature, dispatch/task identity, target plane, nonce, issued/expiry
    timestamps, an allowlisted workload, and candidate source commit/tree/bundle SHA-256.
  - Validation rejects an un-enrolled or forged signer, wrong plane, expiry, a lifetime over 15 minutes, replayed
    requester/nonce, candidate mutation, or workload outside the local allowlist.
  - Responses bind the request digest, actor identity, plane, status, timestamps, bounded artifact metadata, and an
    enrolled actor signature.
  - Success requires a result with no failure; rejection, busy, or failure requires a bounded failure code.
  - The protocol stays dependency-free and fits within the existing one-MiB `bus-msg@1` TCP frame.

---

### LBA-REQ-099: Persistent lbabus autonomous actor service

- Status: Proven
- Area: Deployment / security / operation (ADR-0082)
- Statement: The system shall run each autonomous actor as a persistent cross-platform `lbabus` service that
  durably admits one fixed local workload per signed nonce, stores bounded artifacts in guest-local content-addressed
  storage, and retries cached actor-signed outcomes without re-execution.
- Rationale: The signed protocol needs an actor-owned process that survives duplicate delivery, response loss, and
  restart while preventing request-controlled command execution and concurrent LabVIEW workloads.
- Acceptance Criteria:
  - The daemon launches `lbabus net listen --log` and accepts only `bus-msg@1` `CLAIM` frames whose sender and task
    metadata bind to the signed request.
  - Nonce, signed outcome, and active-task state are atomically persisted; restart does not execute an accepted
    request twice and turns an interrupted active task into a signed bounded failure.
  - Exactly one fixed local adapter executes at a time without a shell or request-derived argv; another valid request
    receives an actor-signed `BUSY` outcome.
  - The actor requires its configured candidate commit, tree, and bundle SHA-256 to equal the signed request.
  - Results are at most 64 KiB; each artifact is at most 16 MiB and stored guest-locally by SHA-256 while only bounded
    metadata crosses the bus.
  - The receive-log cursor advances only after `lbabus net send --message-file` succeeds; retries return the cached
    actor-signed outcome without re-executing.
  - Linux actors run the service under pinned Node 24; Windows actors run only native Windows PowerShell 5 for the
    daemon and fixed LabVIEWCLI workload, using fixed-path OpenSSL solely for protocol-compatible Ed25519 operations.
  - Windows canonical JSON and SHA-256 output match `attest.mjs`, the actor key remains guest-local, inbound `lbabus`
    is narrowly firewall-scoped to the configured port, and Startup persistence survives reboot and interactive logon.
