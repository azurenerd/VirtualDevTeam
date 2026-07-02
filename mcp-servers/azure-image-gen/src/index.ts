#!/usr/bin/env node
// MCP server entry point. Exposes a single tool, `generate_image`, that calls Azure
// OpenAI gpt-image-* with deployment fallback and prompt-refinement retry.
//
// IMPORTANT: this server uses stdout for the JSON-RPC channel. ALL diagnostics must go
// to stderr (console.error). Do NOT use console.log.

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
    CallToolRequestSchema,
    ListToolsRequestSchema,
    type CallToolResult,
} from "@modelcontextprotocol/sdk/types.js";
import { promises as fs } from "node:fs";
import * as path from "node:path";
import { AzureImageClient, type GenerateImageRequest } from "./azureClient.js";
import { verifyPngBuffer } from "./verifier.js";
import { refinePrompt } from "./promptRefiner.js";

const SERVER_NAME = "@vdt/azure-image-gen-mcp";
const SERVER_VERSION = "0.1.0";

type Size = "1024x1024" | "1024x1536" | "1536x1024";
const VALID_SIZES: ReadonlySet<Size> = new Set(["1024x1024", "1024x1536", "1536x1024"]);

interface GenerateImageArgs {
    prompt: string;
    size?: Size;
    reference_image_path?: string;
    output_path: string;
    max_attempts?: number;
}

function isGenerateImageArgs(o: unknown): o is GenerateImageArgs {
    if (typeof o !== "object" || o === null) return false;
    const r = o as Record<string, unknown>;
    if (typeof r.prompt !== "string" || r.prompt.length === 0) return false;
    if (typeof r.output_path !== "string" || r.output_path.length === 0) return false;
    if (r.size !== undefined && (typeof r.size !== "string" || !VALID_SIZES.has(r.size as Size)))
        return false;
    if (r.reference_image_path !== undefined && typeof r.reference_image_path !== "string")
        return false;
    if (
        r.max_attempts !== undefined &&
        (typeof r.max_attempts !== "number" || r.max_attempts < 1 || r.max_attempts > 10)
    )
        return false;
    return true;
}

async function ensureParentDir(filePath: string): Promise<void> {
    const dir = path.dirname(filePath);
    await fs.mkdir(dir, { recursive: true });
}

async function writePngSafely(outputPath: string, png: Buffer): Promise<void> {
    try {
        await fs.access(outputPath);
        console.error(`[index] WARNING: overwriting existing file at ${outputPath}`);
    } catch {
        /* file does not exist — normal path */
    }
    await fs.writeFile(outputPath, png);
}

async function handleGenerateImage(
    client: AzureImageClient,
    args: GenerateImageArgs
): Promise<CallToolResult> {
    const size: Size = args.size ?? "1024x1024";
    const maxAttempts = args.max_attempts ?? 3;
    const outputPath = args.output_path;

    if (!path.isAbsolute(outputPath)) {
        return errorResult(
            `output_path must be absolute. Got: '${outputPath}'. ` +
                "Pass a fully-qualified path like C:\\\\Git\\\\VirtualDevTeam\\\\.agents\\\\generated.png"
        );
    }
    await ensureParentDir(outputPath);

    let currentPrompt = args.prompt;
    let lastFailureReason: string | undefined;
    const attemptLog: Array<{
        attempt: number;
        prompt: string;
        ok: boolean;
        reason?: string;
        deploymentAttempts: unknown;
    }> = [];

    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
        if (attempt > 1) {
            currentPrompt = refinePrompt({
                originalPrompt: args.prompt,
                attemptNumber: attempt,
                size,
                lastFailureReason,
            });
            console.error(
                `[index] retrying with refined prompt (attempt ${attempt}/${maxAttempts}); reason=${lastFailureReason ?? "(none)"}`
            );
        }

        const req: GenerateImageRequest = {
            prompt: currentPrompt,
            size,
            referenceImagePath: args.reference_image_path,
        };
        const gen = await client.generateImage(req);
        if (!gen.ok || !gen.pngBytes) {
            lastFailureReason = gen.error ?? "generation failed";
            attemptLog.push({
                attempt,
                prompt: currentPrompt,
                ok: false,
                reason: lastFailureReason,
                deploymentAttempts: gen.attempts,
            });
            continue;
        }

        const verify = verifyPngBuffer(gen.pngBytes);
        if (!verify.ok) {
            lastFailureReason = verify.reason;
            attemptLog.push({
                attempt,
                prompt: currentPrompt,
                ok: false,
                reason: `verification failed: ${verify.reason}`,
                deploymentAttempts: gen.attempts,
            });
            continue;
        }

        await writePngSafely(outputPath, gen.pngBytes);
        attemptLog.push({
            attempt,
            prompt: currentPrompt,
            ok: true,
            deploymentAttempts: gen.attempts,
        });

        const payload = {
            ok: true,
            output_path: outputPath,
            bytes: verify.bytes,
            deployment_used: gen.deployment,
            attempts: attemptLog,
        };
        return {
            content: [
                {
                    type: "text",
                    text:
                        `Image generated successfully.\n` +
                        `path: ${outputPath}\n` +
                        `bytes: ${verify.bytes}\n` +
                        `deployment: ${gen.deployment}\n` +
                        `attempts: ${attempt}/${maxAttempts}\n\n` +
                        `Full report:\n${JSON.stringify(payload, null, 2)}`,
                },
            ],
        };
    }

    const failurePayload = {
        ok: false,
        error: `Exhausted ${maxAttempts} attempts.`,
        attempts: attemptLog,
        last_failure_reason: lastFailureReason,
    };
    return errorResult(
        `Image generation failed after ${maxAttempts} attempt(s). Last reason: ${lastFailureReason ?? "unknown"}.\n` +
            `Full attempt log:\n${JSON.stringify(failurePayload, null, 2)}`
    );
}

function errorResult(message: string): CallToolResult {
    return {
        isError: true,
        content: [{ type: "text", text: message }],
    };
}

async function main(): Promise<void> {
    let client: AzureImageClient;
    try {
        client = AzureImageClient.fromEnv();
    } catch (err) {
        console.error(`[index] FATAL: ${(err as Error).message}`);
        process.exit(2);
    }

    console.error(
        `[index] starting ${SERVER_NAME}@${SERVER_VERSION}; deployments=${JSON.stringify(client.deployments)}`
    );

    const server = new Server(
        { name: SERVER_NAME, version: SERVER_VERSION },
        { capabilities: { tools: {} } }
    );

    server.setRequestHandler(ListToolsRequestSchema, async () => ({
        tools: [
            {
                name: "generate_image",
                description:
                    "Generate an image via Azure OpenAI gpt-image-* with automatic deployment fallback and verification retry. Returns the saved file path.",
                inputSchema: {
                    type: "object",
                    properties: {
                        prompt: {
                            type: "string",
                            description:
                                "Detailed image prompt. Be very specific — subtle effects need strong cues.",
                        },
                        size: {
                            type: "string",
                            enum: ["1024x1024", "1024x1536", "1536x1024"],
                            default: "1024x1024",
                        },
                        reference_image_path: {
                            type: "string",
                            description:
                                "Optional: absolute path to a reference image. When provided, the model uses it as a style anchor.",
                            nullable: true,
                        },
                        output_path: {
                            type: "string",
                            description:
                                "Absolute path where the resulting PNG should be saved. Parent directory will be created if needed.",
                        },
                        max_attempts: {
                            type: "number",
                            default: 3,
                            description:
                                "Max attempts (with prompt refinement on each retry) before giving up.",
                        },
                    },
                    required: ["prompt", "output_path"],
                },
            },
        ],
    }));

    server.setRequestHandler(CallToolRequestSchema, async (request) => {
        if (request.params.name !== "generate_image") {
            return errorResult(`Unknown tool: '${request.params.name}'`);
        }
        const args = request.params.arguments;
        if (!isGenerateImageArgs(args)) {
            return errorResult(
                "Invalid arguments. Required: { prompt: string (non-empty), output_path: string (absolute) }. " +
                    "Optional: { size in ['1024x1024','1024x1536','1536x1024'], reference_image_path: string, max_attempts: 1..10 }"
            );
        }
        try {
            return await handleGenerateImage(client, args);
        } catch (err) {
            const msg = (err as Error).stack ?? (err as Error).message ?? String(err);
            console.error(`[index] unexpected error in generate_image: ${msg}`);
            return errorResult(`Unexpected error: ${(err as Error).message}`);
        }
    });

    const transport = new StdioServerTransport();
    await server.connect(transport);
    console.error("[index] MCP server connected on stdio");
}

main().catch((err) => {
    console.error(`[index] fatal: ${(err as Error).stack ?? (err as Error).message}`);
    process.exit(1);
});
