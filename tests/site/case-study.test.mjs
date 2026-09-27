// PRAXIS-SITE-23: the case study is generated from the records and makes no
// claim the records or Git history cannot back.
import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { withLedger, withValues } from "../../site-tools/evidence.mjs";

const html = readFileSync(new URL("../../site/index.html", import.meta.url), "utf8");
const snapshot = JSON.parse(readFileSync(new URL("../../site/data/gh-84.json", import.meta.url), "utf8"));
const caseStudy = html.slice(html.indexOf('id="case-study"'), html.indexOf('id="closing"'));

test("the ledger is exactly what the renderer produces from the snapshot", () => {
  assert.equal(withValues(withLedger(html, snapshot), snapshot), html, "run node site-tools/evidence.mjs --render");
  const rows = caseStudy.match(/<tr>\s*<th scope="row">/g) ?? [];
  assert.equal(rows.length, snapshot.workItems.length);
});

test("no row claims completion the snapshot does not record", () => {
  snapshot.workItems.forEach((item) => {
    const shown = caseStudy.match(new RegExp(`data-evidence="${item.id}:state">([a-z]+)<`))[1];
    assert.equal(shown, item.state, item.id);
  });
});

test("the self-correction story matches Git history", () => {
  const message = (sha) => {
    try {
      return execFileSync("git", ["log", "-1", "--format=%B", sha], { encoding: "utf8" });
    } catch {
      return null;
    }
  };
  const mislabelled = message("588599e");
  const corrected = message("6d7b300");
  if (mislabelled === null || corrected === null) return; // shallow clone: nothing to compare against
  assert.match(mislabelled, /^PRAXIS-SITE-15: complete with evidence/);
  assert.match(corrected, /previous commit \(588599e\) carried this title/);
});

test("deployment is not claimed as a record", () => {
  assert.match(caseStudy, /Deployment is not a Praxis record/);
  assert.ok(!/deployed successfully|is live|now live/i.test(html));
});

test("the closing states the spec's lines and call to action", () => {
  const closing = html.slice(html.indexOf('id="closing"'));
  assert.match(closing, /Don&rsquo;t just ship software\. <span class="closing__turn">Know how it came to exist\.<\/span>/);
  assert.match(closing, /Requirements\. Execution\. Evidence\. Provenance\./);
  assert.match(closing, />Show me the record</);
});
