#!/usr/bin/env bash
# After Discover fetched the accounts a merged submission PR added to the include
# list, and the site is deployed: tell the submission issue where the page is,
# and offer the README badge. Run by discover.yml from the repo root, with
# GH_TOKEN, REPO, SHA (the pushed commit) and LOGINS (comma-separated).
set -euo pipefail

# The PR this push merged, and the issues its body closes ("Closes #N"). A push
# that isn't a submission PR (an edit by hand) has none, and gets no comment.
body=$(gh api "repos/$REPO/commits/$SHA/pulls" --jq '.[0].body // ""')
issues=$(grep -oiE 'closes #[0-9]+' <<<"$body" | grep -oE '[0-9]+' | sort -u || true)
if [ -z "$issues" ]; then
  echo "No submission issue for $SHA; nothing to say."
  exit 0
fi

site=$(jq -r .siteUrl config/site.json)

# 200 once the deployed page is served; a few tries, as the deploy has only just finished.
live() {
  for _ in 1 2 3 4 5 6; do
    curl -sfo /dev/null "$1" && return 0
    sleep 10
  done
  return 1
}

comment=""
IFS=',' read -ra logins <<<"$LOGINS"
for login in "${logins[@]}"; do
  page="$site/@$(tr 'A-Z' 'a-z' <<<"$login")/"
  if live "$page"; then
    comment+="მადლობა! **$login** უკვე git.ge-ზეა: $page
თუ გსურთ, ეს ნიშანი README-ში ჩასვით:

Thanks! **$login** is now on git.ge: $page
If you like, add this badge to your README:

[![listed on git.ge]($site/badge.svg)]($page)

\`\`\`markdown
[![listed on git.ge]($site/badge.svg)]($page)
\`\`\`

"
  else
    comment+="მადლობა! **$login** სიაში დაემატა, მაგრამ მის პროექტებს შორის ჯერ არცერთი არ აკმაყოფილებს სიის წესებს ($site/about/). ისინი ავტომატურად გამოჩნდება, როცა დააკმაყოფილებენ.

Thanks! **$login** has been added, but none of the account's projects meets the listing rules yet ($site/about/). They'll appear automatically once they do.

"
  fi
done

for issue in $issues; do
  gh issue comment "$issue" --repo "$REPO" --body "$comment"
done
