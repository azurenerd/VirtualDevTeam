// folder-picker.js — Native folder picker via showDirectoryPicker() API
// Chromium-only (Chrome, Edge, Brave). Falls back gracefully on Firefox/Safari.

/**
 * Opens a native folder picker dialog and returns the selected path.
 * Uses the File System Access API (showDirectoryPicker).
 * Returns the folder name (not full path — browsers restrict full path access).
 * The .NET side validates via server-side path resolution.
 * 
 * @param {string} startIn - Hint for starting directory ('desktop', 'documents', 'downloads')
 * @returns {Promise<string|null>} Selected folder name, or null if cancelled/unsupported
 */
window.pickFolder = async function (startIn) {
    if (typeof window.showDirectoryPicker !== 'function') {
        return null; // Not supported (Firefox, Safari, older browsers)
    }

    try {
        const dirHandle = await window.showDirectoryPicker({
            mode: 'read',
            startIn: startIn || 'desktop'
        });
        return dirHandle.name;
    } catch (e) {
        if (e.name === 'AbortError') return null; // User cancelled
        console.warn('Folder picker error:', e);
        return null;
    }
};

/**
 * Checks if the native folder picker is available in this browser.
 * @returns {boolean}
 */
window.isFolderPickerSupported = function () {
    return typeof window.showDirectoryPicker === 'function';
};
