"""Notify upstream changes using successful tracking issues as the durable cursor."""
import json
import os
from pathlib import Path
import re
import subprocess


def api(endpoint, **fields):
    command = ["gh", "api", endpoint]
    for key, value in fields.items():
        command += ["-f", f"{key}={value}"]
    return json.loads(subprocess.check_output(command, text=True))


def reported_range(issue, upstream):
    if issue.get("pull_request") or issue.get("user", {}).get("login") != "github-actions[bot]":
        return None
    body = issue.get("body") or ""
    sha = r"([0-9a-f]{40})"
    marker = re.search(r"<!-- upstream-sync: " + re.escape(upstream) + " " + sha + r"\.\.\." + sha + r" -->", body)
    legacy = re.search(r"^Compare: https://github\.com/" + re.escape(upstream) + "/compare/" + sha + r"\.\.\." + sha + r"\s*$", body, re.MULTILINE)
    match = marker or legacy
    return match.groups() if match else None


def notify(repository, upstream, initial_sha, request=api):
    latest = request(f"repos/{upstream}/commits/main")["sha"]
    # Include closed reports and every page; closing a report must not notify again.
    reports = []
    page = 1
    while True:
        issues = request(f"repos/{repository}/issues?state=all&sort=created&direction=desc&per_page=100&page={page}")
        reports.extend((issue["number"], reported_range(issue, upstream)) for issue in issues)
        if len(issues) < 100:
            break
        page += 1
    ranges = [(number, pair) for number, pair in reports if pair]
    last = max(ranges, default=(0, (None, initial_sha)), key=lambda item: item[0])[1][1]
    if latest == last or any(pair[1] == latest for _, pair in ranges):
        print(f"Upstream {latest} already reported.")
        return
    if not last:
        raise RuntimeError("No upstream baseline or successful tracking report; set the initial SHA before enabling the watcher.")
    comparison = request(f"repos/{upstream}/compare/{last}...{latest}")
    commits = "\n".join(f'- [{commit["sha"][:7]}]({commit["html_url"]}) {commit["commit"]["message"].splitlines()[0]}' for commit in comparison["commits"])
    body = (
        f"<!-- upstream-sync: {upstream} {last}...{latest} -->\n"
        f"New commits landed on [{upstream}](https://github.com/{upstream}) since the last check.\n\n"
        f"{commits}\n\nCompare: https://github.com/{upstream}/compare/{last}...{latest}\n\n"
        "Review whether any of these are worth porting into this repo's C# implementation (see ROADMAP.md for what's already ported).\n"
    )
    # Only successful creation advances the cursor. No repository write follows it.
    request(f"repos/{repository}/issues",
            title=f"Upstream changes in teknoparrot-manager ({last[:7]}...{latest[:7]})",
            body=body, **{"labels[]": "upstream-sync"})


if __name__ == "__main__":
    notify(os.environ["GITHUB_REPOSITORY"], os.environ["UPSTREAM_REPO"],
           Path(os.environ["STATE_FILE"]).read_text().strip())
