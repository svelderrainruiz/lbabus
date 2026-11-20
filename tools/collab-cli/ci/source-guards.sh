#!/usr/bin/env bash
# Static source guards that labview-benchmark-actor (LBA) used to assert against its in-tree copy of lbabus
# (labview-benchmark-actor#57, slice 3). They live here now, so a regression fails lbabus's own CI before a release
# is cut. Behavioural checks are harness cases under ci/cases; these cover what a case cannot observe.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

fail=0
need() { # need <file> <fixed-string> <message>
  if ! grep -qF -- "$2" "$1"; then echo "FAIL: $3 ($1 lacks: $2)" >&2; fail=1; fi
}
absent() { # absent <file> <extended-regex> <message>
  if grep -qE -- "$2" "$1"; then echo "FAIL: $3 ($1 matches: $2)" >&2; fail=1; fi
}

# LBA gate release-component-versioning / LBA-REQ-095: every external command goes through the stable resolver.
need Dependencies.cs 'internal static string ResolveCommand' 'the stable dependency resolver must stay class-level reusable code'
for f in AgentGuards.cs Agents.cs GitHubGraphQL.cs; do
  need "$f" 'Preflight.ResolveCommand' "$f must not bypass the stable dependency resolver"
done

# LBA gate cli-no-discussion-transport / LBA-REQ-067: the GitHub-Discussion transport stays removed.
absent GitHubGraphQL.cs 'FindDiscussion|CreateDiscussion|EnsureDiscussion|addDiscussionComment|DiscussionRef' 'GitHubGraphQL must have no Discussion surface'
need GitHubGraphQL.cs 'ListReleaseTags' 'GitHubGraphQL keeps the release listing for selfcheck'
need GitHubGraphQL.cs 'AddIssueComment' 'GitHubGraphQL keeps the issue comment for defect'
absent Config.cs 'Category|Title|AddressesMe' 'Config must not carry the discussion-only fields'
absent Program.cs '\b(CmdPost|CmdPoll|CmdWait|CmdInit|CmdDelta)\b' 'Program.cs must not carry the Discussion command methods'
need Program.cs '"net" => NetCommands.Run' 'the live-only net transport must stay dispatched'
need Program.cs '"defect" => CmdDefect' 'the defect command must stay dispatched'
need Program.cs 'CmdSelfCheck' 'the selfcheck command must stay dispatched'

# LBA gate net-coordination-log / net-default-graceful: the dispatch and graceful branches exist.
grep -qE '"poll"[[:space:]]*=>[[:space:]]*CmdPoll' Net.cs || { echo 'FAIL: net dispatch must route poll -> CmdPoll' >&2; fail=1; }
need Net.cs 'private static int CmdPoll(' 'CmdPoll must exist'
need Net.cs 'skip-if-no-peer' 'net send --skip-if-no-peer must exist'

# LBA gate collab-cli-embeds-canonical-requirements: the requirement docs are embedded by reference and registered.
for pair in 'docs/requirements/srs.md:docs.requirements.srs.md' 'docs/requirements/rtm.csv:docs.requirements.rtm.csv'; do
  inc="../../${pair%%:*}"; logical="${pair##*:}"
  need LbaBus.csproj "Include=\"$inc\"" "the csproj must embed $inc by reference"
  need LbaBus.csproj "<LogicalName>$logical</LogicalName>" "the csproj must pin the $logical manifest name"
done
for id in '"srs"' '"rtm"' '"guide"'; do need Docs.cs "$id" "the docs registry must define $id"; done

if [ "$fail" -ne 0 ]; then exit 1; fi
echo "source guards passed"
