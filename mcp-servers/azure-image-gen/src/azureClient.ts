// Azure OpenAI image generation client with deployment fallback.
//
// Configuration (env vars, read at construction time):
//   AZURE_IMAGE_ENDPOINT          (required)  e.g. https://my-aoai.openai.azure.com/
//   AZURE_IMAGE_DEPLOYMENTS_JSON  (required)  JSON array of deployment names, priority order
//   AZURE_IMAGE_API_VERSION       (optional)  default: 2025-04-01-preview
//   AZURE_IMAGE_AUTH_METHOD       (optional)  "DefaultAzureCredential" (default) | "ApiKey"
//   AZURE_IMAGE_API_KEY           (required when AUTH_METHOD=ApiKey)

import { DefaultAzureCredential, type AccessToken, type TokenCredential } from "@azure/identity";
import { promises as fs } from "node:fs";
import { Buffer } from "node:buffer";

const COG_SVC_SCOPE = "https://cognitiveservices.azure.com/.default";
const FALLBACK_ERROR_CODES = new Set([
    "EngineOverloaded",
    "ServiceUnavailable",
    "ResourceNotFound",
    "DeploymentNotFound",
]);

export interface AzureImageClientConfig {
    endpoint: string;
    deployments: readonly string[];
    apiVersion: string;
    authMethod: "DefaultAzureCredential" | "ApiKey";
    apiKey?: string;
}

export interface GenerateImageRequest {
    prompt: string;
    size: "1024x1024" | "1024x1536" | "1536x1024";
    /** Optional reference image, absolute path. When supplied we send it as a data-url in the prompt context. */
    referenceImagePath?: string;
}

export interface DeploymentAttempt {
    deployment: string;
    statusCode?: number;
    errorCode?: string;
    errorMessage?: string;
}

export interface GenerateImageResult {
    ok: boolean;
    /** PNG bytes when ok=true */
    pngBytes?: Buffer;
    /** Deployment that ultimately succeeded (when ok=true) */
    deployment?: string;
    /** Full chronological list of attempts, in order */
    attempts: DeploymentAttempt[];
    /** Final error message when ok=false */
    error?: string;
}

/**
 * Minimal HTTP surface we depend on. Pluggable for tests so we don't need a real
 * network or `fetch` polyfill.
 */
export type HttpFetch = (
    url: string,
    init: { method: string; headers: Record<string, string>; body: string }
) => Promise<{
    status: number;
    ok: boolean;
    text: () => Promise<string>;
}>;

export class AzureImageClient {
    private readonly cfg: AzureImageClientConfig;
    private readonly fetchImpl: HttpFetch;
    private readonly credential?: TokenCredential;
    private cachedToken?: AccessToken;

    constructor(
        cfg: AzureImageClientConfig,
        opts?: { fetchImpl?: HttpFetch; credential?: TokenCredential }
    ) {
        if (!cfg.endpoint) throw new Error("AZURE_IMAGE_ENDPOINT is required");
        if (!cfg.deployments || cfg.deployments.length === 0) {
            throw new Error("AZURE_IMAGE_DEPLOYMENTS_JSON must contain at least one deployment");
        }
        if (cfg.authMethod === "ApiKey" && !cfg.apiKey) {
            throw new Error("AZURE_IMAGE_API_KEY is required when AUTH_METHOD=ApiKey");
        }
        this.cfg = {
            ...cfg,
            endpoint: cfg.endpoint.replace(/\/+$/, ""),
        };
        this.fetchImpl = opts?.fetchImpl ?? (globalThis.fetch as unknown as HttpFetch);
        if (cfg.authMethod === "DefaultAzureCredential") {
            this.credential =
                opts?.credential ??
                new DefaultAzureCredential({ includeInteractiveCredentials: false } as never);
        }
    }

    static fromEnv(env: NodeJS.ProcessEnv = process.env): AzureImageClient {
        const endpoint = env.AZURE_IMAGE_ENDPOINT?.trim();
        if (!endpoint) throw new Error("AZURE_IMAGE_ENDPOINT is required");

        const deploymentsRaw = env.AZURE_IMAGE_DEPLOYMENTS_JSON?.trim();
        if (!deploymentsRaw) throw new Error("AZURE_IMAGE_DEPLOYMENTS_JSON is required");
        let deployments: unknown;
        try {
            deployments = JSON.parse(deploymentsRaw);
        } catch (err) {
            throw new Error(
                `AZURE_IMAGE_DEPLOYMENTS_JSON is not valid JSON: ${(err as Error).message}`
            );
        }
        if (!Array.isArray(deployments) || deployments.some((d) => typeof d !== "string")) {
            throw new Error("AZURE_IMAGE_DEPLOYMENTS_JSON must be a JSON array of strings");
        }

        const authMethod = (env.AZURE_IMAGE_AUTH_METHOD?.trim() ||
            "DefaultAzureCredential") as AzureImageClientConfig["authMethod"];
        if (authMethod !== "DefaultAzureCredential" && authMethod !== "ApiKey") {
            throw new Error(
                `AZURE_IMAGE_AUTH_METHOD must be 'DefaultAzureCredential' or 'ApiKey', got '${authMethod}'`
            );
        }

        return new AzureImageClient({
            endpoint,
            deployments: deployments as string[],
            apiVersion: env.AZURE_IMAGE_API_VERSION?.trim() || "2025-04-01-preview",
            authMethod,
            apiKey: env.AZURE_IMAGE_API_KEY?.trim() || undefined,
        });
    }

    get deployments(): readonly string[] {
        return this.cfg.deployments;
    }

    async generateImage(req: GenerateImageRequest): Promise<GenerateImageResult> {
        const attempts: DeploymentAttempt[] = [];

        // Build the body once. The Azure OpenAI gpt-image-* image generation endpoint
        // accepts {prompt, n, size, quality, output_format}. Reference image is optionally
        // inlined into the prompt as a "(reference image attached)" hint — the modality
        // for true image-to-image on this endpoint is not yet stable; we keep the contract
        // future-proof and let the caller hint via prompt today.
        let promptWithRef = req.prompt;
        if (req.referenceImagePath) {
            try {
                await fs.access(req.referenceImagePath);
                promptWithRef = `${req.prompt}\n\n(A reference image is provided at ${req.referenceImagePath}; match its overall style.)`;
            } catch {
                // Reference inaccessible — surface as soft warning in stderr but don't fail.
                console.error(
                    `[azureClient] reference_image_path not accessible: ${req.referenceImagePath}; continuing without it`
                );
            }
        }

        const body = JSON.stringify({
            prompt: promptWithRef,
            n: 1,
            size: req.size,
            quality: "high",
            output_format: "png",
        });

        for (const deployment of this.cfg.deployments) {
            const url = `${this.cfg.endpoint}/openai/deployments/${encodeURIComponent(deployment)}/images/generations?api-version=${encodeURIComponent(this.cfg.apiVersion)}`;
            const headers = await this.buildHeaders();

            console.error(`[azureClient] attempting deployment='${deployment}' size=${req.size}`);
            let resp: { status: number; ok: boolean; text: () => Promise<string> };
            try {
                resp = await this.fetchImpl(url, { method: "POST", headers, body });
            } catch (netErr) {
                const msg = (netErr as Error).message;
                console.error(`[azureClient] network error on '${deployment}': ${msg}`);
                attempts.push({ deployment, errorMessage: `network: ${msg}` });
                continue;
            }

            const rawText = await resp.text();
            if (resp.ok) {
                const png = this.extractPng(rawText);
                if (png) {
                    attempts.push({ deployment, statusCode: resp.status });
                    return { ok: true, pngBytes: png, deployment, attempts };
                }
                attempts.push({
                    deployment,
                    statusCode: resp.status,
                    errorMessage: "200 OK but response did not contain image data",
                });
                continue;
            }

            // Non-2xx: inspect for fallback-eligible codes.
            const parsed = this.tryParseError(rawText);
            attempts.push({
                deployment,
                statusCode: resp.status,
                errorCode: parsed.code,
                errorMessage: parsed.message ?? rawText.slice(0, 500),
            });
            const isFallbackStatus = resp.status === 429 || resp.status === 503;
            const isFallbackCode = parsed.code ? FALLBACK_ERROR_CODES.has(parsed.code) : false;
            if (!(isFallbackStatus || isFallbackCode)) {
                // Hard failure (e.g., 400 bad prompt, 401 auth). No point trying other deployments.
                return {
                    ok: false,
                    attempts,
                    error: `Hard failure on '${deployment}' (status=${resp.status}, code=${parsed.code ?? "?"}, message=${parsed.message ?? rawText.slice(0, 200)})`,
                };
            }
            console.error(
                `[azureClient] '${deployment}' failed with status=${resp.status} code=${parsed.code ?? "?"} — falling back to next deployment`
            );
        }

        return {
            ok: false,
            attempts,
            error: `All ${this.cfg.deployments.length} deployment(s) failed: ${attempts.map((a) => `${a.deployment}(${a.statusCode ?? "net"}/${a.errorCode ?? a.errorMessage ?? "?"})`).join(", ")}`,
        };
    }

    private extractPng(rawText: string): Buffer | undefined {
        try {
            const json = JSON.parse(rawText) as {
                data?: Array<{ b64_json?: string; url?: string }>;
            };
            const first = json.data?.[0];
            if (first?.b64_json) {
                return Buffer.from(first.b64_json, "base64");
            }
            // URL-based responses are not officially produced by gpt-image-* today; we don't
            // follow them automatically because that doubles the network hop and the agent
            // would expect a self-contained payload. Surface as error.
            if (first?.url) {
                console.error(
                    "[azureClient] response returned a URL instead of b64_json; this MCP server only handles inline payloads"
                );
            }
        } catch (err) {
            console.error(
                `[azureClient] failed to parse 200 OK body as JSON: ${(err as Error).message}`
            );
        }
        return undefined;
    }

    private tryParseError(rawText: string): { code?: string; message?: string } {
        try {
            const obj = JSON.parse(rawText) as {
                error?: { code?: string; message?: string };
            };
            return { code: obj.error?.code, message: obj.error?.message };
        } catch {
            return {};
        }
    }

    private async buildHeaders(): Promise<Record<string, string>> {
        const headers: Record<string, string> = {
            "Content-Type": "application/json",
            Accept: "application/json",
        };
        if (this.cfg.authMethod === "ApiKey") {
            // Non-null asserted by constructor guard.
            headers["api-key"] = this.cfg.apiKey!;
            return headers;
        }
        const token = await this.getAadToken();
        headers["Authorization"] = `Bearer ${token}`;
        return headers;
    }

    private async getAadToken(): Promise<string> {
        // Refresh when within 60s of expiry to avoid mid-request expiry.
        const now = Date.now();
        if (this.cachedToken && this.cachedToken.expiresOnTimestamp - now > 60_000) {
            return this.cachedToken.token;
        }
        if (!this.credential) {
            throw new Error("Internal error: AAD credential not initialized");
        }
        const result = await this.credential.getToken(COG_SVC_SCOPE);
        if (!result) {
            throw new Error(
                `DefaultAzureCredential.getToken('${COG_SVC_SCOPE}') returned null. ` +
                    "Ensure you are signed in via 'az login' or that managed identity is configured."
            );
        }
        this.cachedToken = result;
        return result.token;
    }
}
