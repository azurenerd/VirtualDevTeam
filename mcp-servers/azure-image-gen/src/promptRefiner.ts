// Prompt refinement strategy.
//
// Used when an attempt fails verification or when a deployment returns a hard error
// that we want to retry under a sharpened prompt. Pure rule-based for v1 — no LLM call.
//
// Rationale: gpt-image-* under-emphasizes subtle visual cues (glows, sparks, motion lines,
// translucent UI). Loud explicit directives produce noticeably better output.

export interface RefineInput {
    originalPrompt: string;
    /** 1-based attempt number we are refining FOR (so attempt=2 means we are about to make the 2nd attempt). */
    attemptNumber: number;
    size: string;
    lastFailureReason?: string;
}

const FIRST_RETRY_SUFFIX =
    "\n\nMake every visual effect MORE pronounced and dramatic. Be very explicit about subtle elements like glows, sparks, motion lines, lighting, color saturation, and depth. Do not understate stylistic cues.";

const SECOND_RETRY_SUFFIX =
    "\n\nThis must clearly read at the requested resolution: emphasize bold silhouettes, high contrast, and unambiguous focal points. Avoid muted tones unless explicitly requested.";

export function refinePrompt(input: RefineInput): string {
    const parts: string[] = [input.originalPrompt.trim()];
    if (input.attemptNumber >= 2) {
        parts.push(FIRST_RETRY_SUFFIX);
    }
    if (input.attemptNumber >= 3) {
        parts.push(SECOND_RETRY_SUFFIX);
        parts.push(`\n\n(Target resolution: ${input.size}.)`);
    }
    if (input.lastFailureReason) {
        parts.push(
            `\n\n(Previous attempt was rejected because: ${input.lastFailureReason}. Address that specifically.)`
        );
    }
    return parts.join("");
}
