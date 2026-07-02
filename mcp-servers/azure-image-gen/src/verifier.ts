// Output verifier.
//
// v1: structural check only — confirm the PNG file is at least MIN_PNG_BYTES (5 KB).
// Anything smaller is almost certainly a blank/error placeholder.
//
// TODO(v2): Call a vision-AI model (gpt-4o or gpt-4.1) to semantically verify the image
// matches the prompt. The .NET side of VDT handles richer multi-modal verification today;
// this MCP server intentionally stays cheap and synchronous.

export const MIN_PNG_BYTES = 5 * 1024;

export interface VerifyResult {
    ok: boolean;
    reason?: string;
    bytes: number;
}

export function verifyPngBuffer(buf: Buffer): VerifyResult {
    if (buf.length < MIN_PNG_BYTES) {
        return {
            ok: false,
            bytes: buf.length,
            reason: `PNG is only ${buf.length} bytes (< ${MIN_PNG_BYTES} threshold); likely blank or error placeholder`,
        };
    }
    // PNG magic: 89 50 4E 47 0D 0A 1A 0A
    const magic = buf.subarray(0, 8);
    const expected = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
    if (!magic.equals(expected)) {
        return {
            ok: false,
            bytes: buf.length,
            reason: "File does not start with PNG magic bytes; output may be corrupt or non-PNG",
        };
    }
    return { ok: true, bytes: buf.length };
}
