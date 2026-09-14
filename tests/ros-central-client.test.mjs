import assert from "node:assert/strict";
import http from "node:http";
import test from "node:test";

import { reportActivity } from "../tools/ros_central_client.mjs";

// Exercises the client's HTTP contract against a plain Node mock server
// standing in for `ros-central` (src/Ros.Host), which is already
// verified for real in tests/Ros.Host.Tests and by a manual end-to-end
// smoke test against a locally running instance (see
// docs/migrations/central-integration/EXECUTION-STATUS.md). This test
// is about the client's own request shape, not re-proving Central's
// behavior.

async function withMockCentral(handler, run) {
  const server = http.createServer(handler);
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address();

  try {
    await run(`http://127.0.0.1:${port}`);
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
}

test("reportActivity POSTs to /integration/v1/activities with the activity as the JSON body", async () => {
  let received = null;

  await withMockCentral(
    (req, res) => {
      let body = "";
      req.on("data", (chunk) => (body += chunk));
      req.on("end", () => {
        received = { method: req.method, url: req.url, headers: req.headers, body: JSON.parse(body) };
        res.writeHead(202, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ activityId: "ACT-CLIENT-0001", state: "accepted" }));
      });
    },
    async (centralUrl) => {
      const activity = { contractVersion: "1", activityId: "ACT-CLIENT-0001", organizationId: "ORG-ECHELON", projectId: "PROJ-ROS", evidence: [] };
      const result = await reportActivity(centralUrl, activity, "test-producer");

      assert.equal(result.status, 202);
      assert.deepEqual(result.body, { activityId: "ACT-CLIENT-0001", state: "accepted" });

      assert.equal(received.method, "POST");
      assert.equal(received.url, "/integration/v1/activities");
      assert.equal(received.headers["x-ros-producer"], "test-producer");
      assert.deepEqual(received.body, activity);
    }
  );
});

test("reportActivity defaults the producer header to 'unspecified' when none is given", async () => {
  let received = null;

  await withMockCentral(
    (req, res) => {
      received = req.headers["x-ros-producer"];
      res.writeHead(202, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ activityId: "ACT-CLIENT-0002", state: "accepted" }));
    },
    async (centralUrl) => {
      await reportActivity(centralUrl, { activityId: "ACT-CLIENT-0002" });
      assert.equal(received, "unspecified");
    }
  );
});

test("reportActivity surfaces a non-2xx status and its error body without throwing", async () => {
  await withMockCentral(
    (_req, res) => {
      res.writeHead(422, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ errors: ["MissingActivityId"] }));
    },
    async (centralUrl) => {
      const result = await reportActivity(centralUrl, {});
      assert.equal(result.status, 422);
      assert.deepEqual(result.body, { errors: ["MissingActivityId"] });
    }
  );
});
