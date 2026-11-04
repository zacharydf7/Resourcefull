// Uploads 3 MiB plus 5 bytes of random content with tus-js-client, in chunks of 1 MiB, to the endpoint given, and prints
// a JSON summary for TusClientTests: whether the client reported success, the content's SHA-256, and the requests it
// sent. With "interrupted" as the scenario, it stops after the first chunk, as a dropped connection would, then resumes
// from the upload's URL with a new client.
//
//   node upload.mjs <endpoint> <filename> [interrupted]
import { createHash, randomBytes } from "node:crypto";
import { Upload } from "tus-js-client";

const [endpoint, filename, scenario] = process.argv.slice(2);
const content = randomBytes(3 * 1024 * 1024 + 5);
const requests = [];

function run(options) {
  return new Promise((resolve) => {
    let upload = null;
    upload = new Upload(content, {
      endpoint,
      chunkSize: 1024 * 1024,
      retryDelays: [0, 50, 100],
      metadata: { filename, filetype: "application/octet-stream" },
      onAfterResponse: (request, response) => {
        requests.push({ method: request.getMethod(), status: response.getStatus(), offset: response.getHeader("Upload-Offset") ?? null });
      },
      onSuccess: () => resolve({ ok: true, url: upload.url }),
      onError: (error) => resolve({ ok: false, url: upload.url, message: String(error) }),
      ...options(() => upload, resolve)
    });
    upload.start();
  });
}

let result;
if (scenario === "interrupted") {
  const first = await run((upload, resolve) => ({
    onChunkComplete: () => upload().abort().then(() => resolve({ ok: false, url: upload().url, message: "aborted" }))
  }));
  requests.push({ method: "ABORT", status: null, offset: null });
  result = await run(() => ({ uploadUrl: first.url }));
} else {
  result = await run(() => ({}));
}

console.log(JSON.stringify({ ...result, sha256: createHash("sha256").update(content).digest("hex"), requests }));
