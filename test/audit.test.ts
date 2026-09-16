import { describe, expect, it } from "vitest";
import { redactSecrets } from "../src/audit.js";

describe("audit redaction", () => {
  it("redacts nested credentials while retaining ordinary values", () => {
    expect(redactSecrets({
      password: "secret",
      nested: { apiKey: "key", token: "token", name: "Drawing1.dwg" },
      items: [{ authorization: "Bearer secret" }],
    })).toEqual({
      password: "[REDACTED]",
      nested: { apiKey: "[REDACTED]", token: "[REDACTED]", name: "Drawing1.dwg" },
      items: [{ authorization: "[REDACTED]" }],
    });
  });
});
