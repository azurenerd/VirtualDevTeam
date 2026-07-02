// Mocked unit tests for AzureImageClient fallback chain.
// Uses Node's built-in node:test (run via `node --test --import tsx tests/*.test.ts`).

import { test } from "node:test";
import assert from "node:assert/strict";
import { Buffer } from "node:buffer";
import { AzureImageClient, type HttpFetch } from "../src/azureClient.js";

const PNG_MAGIC = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
function makeFakePngBase64(sizeBytes = 6 * 1024): string {
    const buf = Buffer.concat([PNG_MAGIC, Buffer.alloc(sizeBytes - PNG_MAGIC.length, 0x42)]);
    return buf.toString("base64");
}

function makeOkResponse() {
    return {
        status: 200,
        ok: true,
        text: async () =>
            JSON.stringify({ data: [{ b64_json: makeFakePngBase64() }] }),
    };
}

function makeErrorResponse(status: number, code: string, message = "boom") {
    return {
        status,
        ok: false,
        text: async () => JSON.stringify({ error: { code, message } }),
    };
}

const baseCfg = {
    endpoint: "https://example.openai.azure.com",
    deployments: ["gpt-image-2", "gpt-image-1", "gpt-image-1-mini"] as const,
    apiVersion: "2025-04-01-preview",
    authMethod: "ApiKey" as const,
    apiKey: "test-key",
};

test("primary deployment succeeds — no fallback used", async () => {
    const calls: string[] = [];
    const fetchImpl: HttpFetch = async (url) => {
        calls.push(url);
        return makeOkResponse();
    };
    const client = new AzureImageClient({ ...baseCfg }, { fetchImpl });
    const res = await client.generateImage({ prompt: "a hamster", size: "1024x1024" });
    assert.equal(res.ok, true);
    assert.equal(res.deployment, "gpt-image-2");
    assert.equal(res.attempts.length, 1);
    assert.equal(calls.length, 1);
    assert.ok(calls[0]!.includes("/openai/deployments/gpt-image-2/images/generations"));
    assert.ok(res.pngBytes && res.pngBytes.length > 5 * 1024);
});

test("primary returns 429 — falls through to second deployment which succeeds", async () => {
    const calls: string[] = [];
    const fetchImpl: HttpFetch = async (url) => {
        calls.push(url);
        if (url.includes("gpt-image-2")) {
            return makeErrorResponse(429, "EngineOverloaded", "rate-limited");
        }
        return makeOkResponse();
    };
    const client = new AzureImageClient({ ...baseCfg }, { fetchImpl });
    const res = await client.generateImage({ prompt: "a hamster", size: "1024x1024" });
    assert.equal(res.ok, true);
    assert.equal(res.deployment, "gpt-image-1");
    assert.equal(res.attempts.length, 2);
    assert.equal(res.attempts[0]!.deployment, "gpt-image-2");
    assert.equal(res.attempts[0]!.statusCode, 429);
    assert.equal(res.attempts[0]!.errorCode, "EngineOverloaded");
    assert.equal(res.attempts[1]!.deployment, "gpt-image-1");
    assert.equal(calls.length, 2);
});

test("all deployments fail — returns error with attempt history", async () => {
    const fetchImpl: HttpFetch = async (url) => {
        if (url.includes("gpt-image-2")) return makeErrorResponse(429, "EngineOverloaded");
        if (url.includes("gpt-image-1-mini")) return makeErrorResponse(503, "ServiceUnavailable");
        return makeErrorResponse(404, "DeploymentNotFound", "no such deployment");
    };
    const client = new AzureImageClient({ ...baseCfg }, { fetchImpl });
    const res = await client.generateImage({ prompt: "a hamster", size: "1024x1024" });
    assert.equal(res.ok, false);
    assert.equal(res.attempts.length, 3);
    assert.ok(res.error && res.error.includes("All 3 deployment(s) failed"));
    assert.equal(res.attempts[0]!.deployment, "gpt-image-2");
    assert.equal(res.attempts[1]!.deployment, "gpt-image-1");
    assert.equal(res.attempts[2]!.deployment, "gpt-image-1-mini");
    assert.equal(res.attempts[0]!.errorCode, "EngineOverloaded");
    assert.equal(res.attempts[1]!.errorCode, "DeploymentNotFound");
    assert.equal(res.attempts[2]!.errorCode, "ServiceUnavailable");
});

test("hard failure (400) does NOT trigger fallback", async () => {
    const calls: string[] = [];
    const fetchImpl: HttpFetch = async (url) => {
        calls.push(url);
        return makeErrorResponse(400, "InvalidPrompt", "policy violation");
    };
    const client = new AzureImageClient({ ...baseCfg }, { fetchImpl });
    const res = await client.generateImage({ prompt: "anything", size: "1024x1024" });
    assert.equal(res.ok, false);
    assert.equal(calls.length, 1, "should not retry on hard 400");
    assert.ok(res.error && res.error.includes("Hard failure"));
});
