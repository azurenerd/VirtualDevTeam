// Scroll-to + highlight helper for the Approvals page deep-link
// (/approvals?focus={GateId}). Invoked from Approvals.razor's
// OnAfterRenderAsync via IJSRuntime.InvokeVoidAsync.
//
// The .highlight-pulse CSS class drives a one-shot animation; this script just
// scrolls the target into view and re-applies the class if the card is
// re-rendered after the initial scroll.
(function () {
    window.vdtScrollAndHighlightGate = function (gateId) {
        if (!gateId) return;
        var doScroll = function (attempt) {
            var el = document.getElementById('gate-' + gateId);
            if (!el) {
                // Card may not have rendered yet — retry briefly. Cap retries
                // so we don't loop forever if the gate is closed/missing.
                if (attempt < 10) {
                    setTimeout(function () { doScroll(attempt + 1); }, 100);
                }
                return;
            }
            try {
                el.scrollIntoView({ behavior: 'smooth', block: 'center' });
            } catch (e) {
                // Older browsers: fall back to a default jump.
                el.scrollIntoView();
            }
            // Re-apply the class in case Blazor's re-render dropped it
            // between OnInitialized and OnAfterRenderAsync.
            el.classList.add('highlight-pulse');
            // Remove after the CSS animation runs (~2.5s) so a subsequent
            // visit doesn't show a stale glow.
            setTimeout(function () {
                el.classList.remove('highlight-pulse');
            }, 2800);
        };
        doScroll(0);
    };
})();
