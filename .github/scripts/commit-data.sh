#!/usr/bin/env bash
# Commit and push changes in the `data` branch checkout (run from data/bot).
# Retries with a rebase in case another run pushed first; the workflows share a
# concurrency group, so this is a safety net rather than the normal path.
set -euo pipefail

message="$1"

git config user.name "github-actions[bot]"
git config user.email "41898283+github-actions[bot]@users.noreply.github.com"

git add -A
if git diff --cached --quiet; then
  echo "No data changes to commit."
  exit 0
fi
git commit -q -m "$message"

for attempt in 1 2 3 4 5; do
  if git push -q origin HEAD:data; then
    echo "Pushed data changes (attempt $attempt)."
    exit 0
  fi
  echo "Push rejected; rebasing onto the latest data branch (attempt $attempt)."
  if ! git pull -q --rebase origin data; then
    git rebase --abort || true
    echo "::error::Rebase onto the data branch failed; data changes were not pushed."
    exit 1
  fi
  sleep $((attempt * 5))
done

echo "::error::Could not push data changes after 5 attempts."
exit 1
