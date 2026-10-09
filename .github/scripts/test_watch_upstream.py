import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("watcher", Path(__file__).with_name("watch_upstream.py"))
watcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(watcher)
UPSTREAM = "Jumpstile/teknoparrot-manager"
A, B, C = "a" * 40, "b" * 40, "c" * 40


def report(number, base=A, head=B, state="open", marker=False):
    body = f"Compare: https://github.com/{UPSTREAM}/compare/{base}...{head}\n"
    if marker:
        body = f"<!-- upstream-sync: {UPSTREAM} {base}...{head} -->"
    return dict(number=number, body=body, state=state, user=dict(login="github-actions[bot]"))


class FakeAPI:
    def __init__(self, reports=None, latest=B, fail=False):
        self.reports = reports or []
        self.latest, self.fail = latest, fail
        self.created, self.pages = [], []

    def __call__(self, endpoint, **fields):
        if fields:
            if self.fail:
                raise RuntimeError("Issue creation failed")
            self.created.append(fields)
            self.reports.insert(0, report(1000, marker=True, head=self.latest, base=B))
            return dict(number=1000)
        if "/commits/main" in endpoint:
            return dict(sha=self.latest)
        if "/compare/" in endpoint:
            return dict(commits=[dict(sha=self.latest, html_url="https://example.test/commit", commit=dict(message="New change\nDetails"))])
        page = int(endpoint.split("page=")[-1])
        self.pages.append(page)
        return self.reports[(page-1)*100:page*100]


class WatcherTests(unittest.TestCase):
    def run_watch(self, api):
        watcher.notify("owner/repo", UPSTREAM, A, api)

    def test_legacy_repeated_range(self):
        api = FakeAPI([report(89)])
        self.run_watch(api)
        self.assertEqual(api.created, [])

    def test_closed_report_on_second_page(self):
        unrelated = [dict(number=n, body="", user=dict(login="someone")) for n in range(200, 300)]
        api = FakeAPI(unrelated + [report(51, state="closed")])
        self.run_watch(api)
        self.assertEqual(api.pages, [1, 2])
        self.assertEqual(api.created, [])

    def test_new_range_creates_once_and_uses_latest_report(self):
        api = FakeAPI([report(89), report(30, head=A)], latest=C)
        self.run_watch(api)
        self.run_watch(api)
        self.assertEqual(len(api.created), 1)
        self.assertIn(f"{B}...{C}", api.created[0]["body"])
        self.assertEqual(api.created[0]["labels[]"], "upstream-sync")

    def test_failure_does_not_advance_and_retry_creates(self):
        api = FakeAPI([report(89)], latest=C, fail=True)
        with self.assertRaises(RuntimeError):
            self.run_watch(api)
        self.assertEqual(len(api.reports), 1)
        api.fail = False
        self.run_watch(api)
        self.assertEqual(len(api.created), 1)

    def test_marker_and_author_filter(self):
        self.assertEqual(watcher.reported_range(report(1, marker=True), UPSTREAM), (A, B))
        issue = report(1)
        issue["user"]["login"] = "human"
        self.assertIsNone(watcher.reported_range(issue, UPSTREAM))

    def test_initial_cursor_no_change(self):
        api = FakeAPI(latest=A)
        self.run_watch(api)
        self.assertEqual(api.created, [])


if __name__ == "__main__":
    unittest.main()
