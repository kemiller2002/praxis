// PRAXIS-SITE-13: an execution record on the page is either populated from
// real Praxis records (every value cites the snapshot) or visibly labelled
// ILLUSTRATIVE EXECUTION. Examples must never pass for evidence.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const html = readFileSync(new URL("../../site/index.html", import.meta.url), "utf8");

const records = [...html.matchAll(/<figure class="record"[^>]*data-record="([a-z]+)"[\s\S]*?<\/figure>/g)].map(([markup, kind]) => ({ kind, markup }));

const values = (markup) => [...markup.matchAll(/<dd\b([^>]*)>([\s\S]*?)<\/dd>/g)].map(([, attributes, inner]) => ({ attributes, inner }));

test("the page has at least one execution record and every record declares its kind", () => {
  assert.ok(records.length >= 1);
  assert.equal((html.match(/<figure class="record"/g) ?? []).length, records.length, "a record without data-record");
  records.forEach(({ kind }) => assert.ok(["illustrative", "real"].includes(kind), kind));
});

test("illustrative records say so in the header and in the caption", () => {
  records
    .filter(({ kind }) => kind === "illustrative")
    .forEach(({ markup }) => {
      assert.match(markup, /class="record__label">Illustrative execution</);
      assert.match(markup, /<figcaption[^>]*>Illustrative execution\./);
      assert.ok(!markup.includes("data-evidence"), "illustrative values must not cite evidence");
    });
});

test("real records cite the snapshot for every value", () => {
  records
    .filter(({ kind }) => kind === "real")
    .forEach(({ markup }) => {
      assert.match(markup, /record__label--real/);
      assert.ok(!/illustrative/i.test(markup));
      values(markup).forEach(({ attributes, inner }) =>
        assert.ok(/data-evidence=/.test(attributes) || /data-evidence=/.test(inner) || /class="none"/.test(attributes), `uncited value: ${inner}`)
      );
    });
});

test("illustrative identifiers cannot be mistaken for real ones", () => {
  records
    .filter(({ kind }) => kind === "illustrative")
    .forEach(({ markup }) => {
      const ids = markup.match(/EXE-[0-9A-Za-z-]+/g) ?? [];
      ids.forEach((id) => assert.match(id, /example/, id));
    });
});
